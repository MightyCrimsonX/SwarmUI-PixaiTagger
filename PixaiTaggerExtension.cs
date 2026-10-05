using System.IO;
using System.Globalization;
using System.Collections.Concurrent;
using System.Linq;
using FreneticUtilities.FreneticExtensions;
using Newtonsoft.Json.Linq;
using SwarmUI.Accounts;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Core;
using SwarmUI.Media;
using SwarmUI.Text2Image;
using SwarmUI.Utils;

namespace SwarmUI.PixaiTagger;

/// <summary>PixAI Tagger Extension - High accuracy Danbooru-style image tagging using PixAI Tagger v1.0 on GPU.</summary>
public class PixaiTaggerExtension : Extension
{
    /// <summary>Path to this extension's directory.</summary>
    public static string ExtFolder;

    /// <summary>ExtraMeta cache key for prompt-tag generated PixAI tags.</summary>
    public const string PromptTagCacheKey = "pixaitagger_prompt_tags";

    /// <summary>Global cache for generated prompt tags across batches and generations, keyed by image hash and tag parameters.</summary>
    public static readonly ConcurrentDictionary<string, string> GlobalTagCache = new();

    /// <summary>Active in-flight prompt tag generation tasks to prevent duplicate concurrent tag executions in batch generations.</summary>
    private static readonly ConcurrentDictionary<string, Task<string>> InFlightPromptTagTasks = new();

    /// <summary>Parameter group for PixAI Tagger controls.</summary>
    public static T2IParamGroup PixaiTaggerGroup;

    /// <summary>Input image to be tagged by PixAI Tagger.</summary>
    public static T2IRegisteredParam<Image> InputImageParam;

    /// <summary>Confidence threshold for general tags.</summary>
    public static T2IRegisteredParam<double> GeneralThresholdParam;

    /// <summary>Confidence threshold for character tags.</summary>
    public static T2IRegisteredParam<double> CharacterThresholdParam;

    /// <summary>Confidence threshold for clothing and attire tags.</summary>
    public static T2IRegisteredParam<double> ClothingThresholdParam;

    /// <summary>Confidence threshold for art style tags.</summary>
    public static T2IRegisteredParam<double> StyleThresholdParam;

    /// <summary>Confidence threshold for copyright / series tags.</summary>
    public static T2IRegisteredParam<double> CopyrightThresholdParam;

    /// <summary>Whether to include numerical confidence scores in the output tags.</summary>
    public static T2IRegisteredParam<bool> IncludeConfidenceParam;

    /// <summary>Whether to keep underscores in tag names instead of converting them to spaces.</summary>
    public static T2IRegisteredParam<bool> KeepUnderscoresParam;

    /// <summary>Filter rules to exclude or replace tags.</summary>
    public static T2IRegisteredParam<string> FilterTagsParam;

    /// <summary>How generated tags are inserted into the prompt box.</summary>
    public static T2IRegisteredParam<string> InsertModeParam;

    /// <summary>Retrieves the image to be tagged when executing a prompt tag.</summary>
    private static Image GetPromptTagImageSource(T2IParamInput input)
    {
        if (input.TryGet(InputImageParam, out Image taggerImage) && taggerImage is not null)
        {
            return taggerImage;
        }
        if (input.TryGet(T2IParamTypes.InitImage, out Image initImage) && initImage is not null)
        {
            return initImage;
        }
        if (input.TryGet(T2IParamTypes.PromptImages, out List<Image> promptImages) && promptImages is not null && promptImages.Count > 0)
        {
            return promptImages[0];
        }
        return null;
    }

    /// <summary>Parses prompt tag positional arguments: [general_threshold, character_threshold, clothing_threshold].</summary>
    private static void ResolvePromptTagThresholds(string data, T2IParamInput input,
        out float genThresh, out bool enableGen,
        out float charThresh, out bool enableChar,
        out float clothThresh, out bool enableCloth,
        out float styleThresh, out bool enableStyle,
        out float copyThresh, out bool enableCopy)
    {
        enableGen = input.TryGet(GeneralThresholdParam, out double g);
        genThresh = enableGen ? (float)g : PixaiTaggerAPI.DefaultGeneralThreshold;

        enableChar = input.TryGet(CharacterThresholdParam, out double c);
        charThresh = enableChar ? (float)c : PixaiTaggerAPI.DefaultCharacterThreshold;

        enableCloth = input.TryGet(ClothingThresholdParam, out double cl);
        clothThresh = enableCloth ? (float)cl : PixaiTaggerAPI.DefaultClothingThreshold;

        enableStyle = input.TryGet(StyleThresholdParam, out double s);
        styleThresh = enableStyle ? (float)s : PixaiTaggerAPI.DefaultStyleThreshold;

        enableCopy = input.TryGet(CopyrightThresholdParam, out double cp);
        copyThresh = enableCopy ? (float)cp : PixaiTaggerAPI.DefaultCopyrightThreshold;

        // If none of the parameters were toggled in input (e.g. prompt tag used alone), fall back to defaults
        if (!enableGen && !enableChar && !enableCloth && !enableStyle && !enableCopy)
        {
            enableGen = true;
            enableChar = true;
            enableCloth = true;
            enableStyle = false;
            enableCopy = false;
        }

        if (string.IsNullOrWhiteSpace(data))
        {
            return;
        }

        string[] parts = data.Split([','], StringSplitOptions.TrimEntries);
        if (parts.Length > 0 && float.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out float parsedG))
        {
            genThresh = Math.Clamp(parsedG, 0f, 1f);
            enableGen = true;
        }
        if (parts.Length > 1 && float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float parsedC))
        {
            charThresh = Math.Clamp(parsedC, 0f, 1f);
            enableChar = true;
        }
        if (parts.Length > 2 && float.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out float parsedCl))
        {
            clothThresh = Math.Clamp(parsedCl, 0f, 1f);
            enableCloth = true;
        }
    }

    /// <summary>Expands the &lt;pixaitagger&gt; prompt tag into tags from the init image, with cross-generation caching and batch deduplication.</summary>
    private static string GeneratePromptTagTags(string data, T2IPromptHandling.PromptTagContext context)
    {
        if (context?.Input is null)
        {
            return "";
        }

        ResolvePromptTagThresholds(data, context.Input,
            out float genThresh, out bool enableGen,
            out float charThresh, out bool enableChar,
            out float clothThresh, out bool enableCloth,
            out float styleThresh, out bool enableStyle,
            out float copyThresh, out bool enableCopy);
        string filterTags = context.Input.Get(FilterTagsParam, "");
        bool includeConf = context.Input.Get(IncludeConfidenceParam, false);
        bool keepUnder = context.Input.Get(KeepUnderscoresParam, false);

        Image source = GetPromptTagImageSource(context.Input);
        if (source is null)
        {
            context.TrackWarning("PixAI Tagger: Prompt tag '<pixaitagger>' used, but no image was provided in [PixAI] Image, Init Image, or prompt images.");
            return "";
        }

        string imgHash;
        try
        {
            byte[] imageBytes = source.RawData ?? (string.IsNullOrEmpty(source.AsBase64) ? null : Convert.FromBase64String(source.AsBase64));
            imgHash = imageBytes is not null ? Utilities.HashSHA256(imageBytes) : $"{source.AsBase64.Length}:{(source.AsBase64.Length > 32 ? source.AsBase64[..32] : source.AsBase64)}";
        }
        catch
        {
            imgHash = $"{source.AsBase64.Length}:{(source.AsBase64.Length > 32 ? source.AsBase64[..32] : source.AsBase64)}";
        }

        string cacheKey = PixaiTaggerAPI.BuildGpuCacheKey(
            imgHash,
            genThresh, enableGen,
            charThresh, enableChar,
            clothThresh, enableCloth,
            styleThresh, enableStyle,
            copyThresh, enableCopy,
            includeConf, keepUnder) + $"|filter:{filterTags}";

        // Check if already tagged previously across generations
        if (GlobalTagCache.TryGetValue(cacheKey, out string cachedTags))
        {
            string shortHash = imgHash.Length > 12 ? imgHash[..12] : imgHash;
            Logs.Verbose($"[PixAITagger] Reusing cached tags for <pixaitagger> (SHA256: {shortHash}...). Skipping tag generation.");
            return cachedTags;
        }

        // Deduplicate concurrent batch generation items so GPU execution only runs once
        Task<string> tagTask;
        lock (InFlightPromptTagTasks)
        {
            if (!InFlightPromptTagTasks.TryGetValue(cacheKey, out tagTask))
            {
                tagTask = Task.Run(async () =>
                {
                    try
                    {
                        JObject result = await PixaiTaggerAPI.PixaiTaggerGenerateTags(
                            context.Input.SourceSession,
                            source.AsBase64,
                            generalThreshold: genThresh,
                            characterThreshold: charThresh,
                            clothingThreshold: clothThresh,
                            styleThreshold: styleThresh,
                            copyrightThreshold: copyThresh,
                            enableGeneral: enableGen,
                            enableCharacter: enableChar,
                            enableClothing: enableCloth,
                            enableStyle: enableStyle,
                            enableCopyright: enableCopy,
                            includeConfidence: includeConf,
                            keepUnderscores: keepUnder,
                            filterTags: filterTags
                        );

                        if (result?["success"]?.Value<bool>() != true)
                        {
                            string err = result?["error"]?.Value<string>() ?? "Unknown PixAI error.";
                            context.TrackWarning($"PixAI Tagger prompt tag failed: {err}");
                            return "";
                        }

                        string tags = result?["tags"]?.Value<string>() ?? "";
                        if (GlobalTagCache.Count > 500)
                        {
                            foreach (string oldKey in GlobalTagCache.Keys.Take(250))
                            {
                                GlobalTagCache.TryRemove(oldKey, out _);
                            }
                        }
                        GlobalTagCache[cacheKey] = tags;
                        return tags;
                    }
                    catch (Exception ex)
                    {
                        context.TrackWarning($"PixAI Tagger prompt tag exception: {ex.Message}");
                        return "";
                    }
                    finally
                    {
                        lock (InFlightPromptTagTasks)
                        {
                            InFlightPromptTagTasks.TryRemove(cacheKey, out _);
                        }
                    }
                });
                InFlightPromptTagTasks[cacheKey] = tagTask;
            }
        }

        string generatedTags = tagTask.GetAwaiter().GetResult();
        return generatedTags ?? "";
    }

    public override void OnPreInit()
    {
        ExtFolder = FilePath;
        ScriptFiles.Add("Assets/pixaitagger.js");
        StyleSheetFiles.Add("Assets/pixaitagger.css");
        ComfyUISelfStartBackend.CustomNodePaths.Add(Path.GetFullPath($"{FilePath}/ComfyNodes"));

        PixaiTaggerGroup = new(
            "PixAI Tagger",
            Toggles: true,
            Open: false,
            OrderPriority: 95,
            Description: "Settings for PixAI Tagger v1.0 (Generate Tags button, interactive studio tool, and <pixaitagger> prompt tag).\nOperates on GPU via ComfyUI backend with dedicated clothing thresholding."
        );

        InputImageParam = T2IParamTypes.Register<Image>(new(
            Name: "[PixAI] Image",
            Description: "Image to tag with PixAI Tagger when using the '<pixaitagger>' prompt tag or interactive tagging.",
            Default: null,
            ImageShouldResize: false,
            Group: PixaiTaggerGroup,
            OrderPriority: 0
        ));

        GeneralThresholdParam = T2IParamTypes.Register<double>(new(
            Name: "[PixAI] General Threshold",
            Description: "Minimum confidence threshold for general tags (actions, environment, objects). Default: 0.17",
            Default: "0.17",
            Min: 0.0,
            Max: 1.0,
            Step: 0.01,
            ViewMin: 0.0,
            ViewMax: 1.0,
            ViewType: ParamViewType.SLIDER,
            Toggleable: true,
            Group: PixaiTaggerGroup,
            OrderPriority: 1
        ));

        CharacterThresholdParam = T2IParamTypes.Register<double>(new(
            Name: "[PixAI] Character Threshold",
            Description: "Minimum confidence threshold for recognized character names. Default: 0.27",
            Default: "0.27",
            Min: 0.0,
            Max: 1.0,
            Step: 0.01,
            ViewMin: 0.0,
            ViewMax: 1.0,
            ViewType: ParamViewType.SLIDER,
            Toggleable: true,
            Group: PixaiTaggerGroup,
            OrderPriority: 2
        ));

        ClothingThresholdParam = T2IParamTypes.Register<double>(new(
            Name: "[PixAI] Clothing Threshold",
            Description: "Minimum confidence threshold for clothing, garments, and attire tags. Default: 0.17",
            Default: "0.17",
            Min: 0.0,
            Max: 1.0,
            Step: 0.01,
            ViewMin: 0.0,
            ViewMax: 1.0,
            ViewType: ParamViewType.SLIDER,
            Toggleable: true,
            Group: PixaiTaggerGroup,
            OrderPriority: 3
        ));

        StyleThresholdParam = T2IParamTypes.Register<double>(new(
            Name: "[PixAI] Style Threshold",
            Description: "Minimum confidence threshold for art style and aesthetic tags. Default: 0.15",
            Default: "0.15",
            Min: 0.0,
            Max: 1.0,
            Step: 0.01,
            ViewMin: 0.0,
            ViewMax: 1.0,
            ViewType: ParamViewType.SLIDER,
            Toggleable: true,
            Group: PixaiTaggerGroup,
            OrderPriority: 4
        ));

        CopyrightThresholdParam = T2IParamTypes.Register<double>(new(
            Name: "[PixAI] Copyright / Series Threshold",
            Description: "Minimum confidence threshold for series, franchise, and work IP tags. Default: 0.24",
            Default: "0.24",
            Min: 0.0,
            Max: 1.0,
            Step: 0.01,
            ViewMin: 0.0,
            ViewMax: 1.0,
            ViewType: ParamViewType.SLIDER,
            Toggleable: true,
            Group: PixaiTaggerGroup,
            OrderPriority: 5
        ));

        IncludeConfidenceParam = T2IParamTypes.Register<bool>(new(
            Name: "[PixAI] Include Confidence Scores",
            Description: "Appends confidence scores to tags (e.g. tag:0.95).",
            Default: "false",
            Group: PixaiTaggerGroup,
            OrderPriority: 6
        ));

        KeepUnderscoresParam = T2IParamTypes.Register<bool>(new(
            Name: "[PixAI] Keep Underscores",
            Description: "Preserves underscores in tag names (e.g. school_uniform) instead of converting them to spaces.",
            Default: "false",
            Group: PixaiTaggerGroup,
            OrderPriority: 7
        ));

        FilterTagsParam = T2IParamTypes.Register<string>(new(
            Name: "[PixAI] Filter Tags",
            Description: "Comma-separated list of tags to exclude or replace. Format: 'tag_to_exclude', or 'original:replacement'. Supports wildcard patterns like 'hair*' or '*skirt'.",
            Default: "",
            Group: PixaiTaggerGroup,
            OrderPriority: 8
        ));

        InsertModeParam = T2IParamTypes.Register<string>(new(
            Name: "[PixAI] Insert Mode",
            Description: "How generated tags are inserted into the prompt box.",
            Default: "replace",
            GetValues: _ => ["replace///Replace prompt", "prepend///Prepend to prompt", "append///Append to prompt"],
            Group: PixaiTaggerGroup,
            OrderPriority: 9
        ));

        T2IPromptHandling.PromptTagProcessors["pixaitagger"] = GeneratePromptTagTags;
    }

    public override void OnInit()
    {
        PixaiTaggerAPI.Register();
    }
}
