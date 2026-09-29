using System.IO;
using System.Globalization;
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

    /// <summary>Parameter group for PixAI Tagger controls.</summary>
    public static T2IParamGroup PixaiTaggerGroup;

    /// <summary>Confidence threshold for general tags.</summary>
    public static T2IRegisteredParam<double> GeneralThresholdParam;

    /// <summary>Confidence threshold for character tags.</summary>
    public static T2IRegisteredParam<double> CharacterThresholdParam;

    /// <summary>Confidence threshold for art style tags.</summary>
    public static T2IRegisteredParam<double> StyleThresholdParam;

    /// <summary>Confidence threshold for copyright / series tags.</summary>
    public static T2IRegisteredParam<double> CopyrightThresholdParam;

    /// <summary>Confidence threshold for clothing and attire tags.</summary>
    public static T2IRegisteredParam<double> ClothingThresholdParam;

    /// <summary>Confidence threshold for meta tags.</summary>
    public static T2IRegisteredParam<double> MetaThresholdParam;

    /// <summary>Confidence threshold for rating tags.</summary>
    public static T2IRegisteredParam<double> RatingThresholdParam;

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
    private static void ResolvePromptTagThresholds(string data, T2IParamInput input, out float genThresh, out float charThresh, out float clothThresh)
    {
        genThresh = input.TryGet(GeneralThresholdParam, out double g) ? (float)g : PixaiTaggerAPI.DefaultGeneralThreshold;
        charThresh = input.TryGet(CharacterThresholdParam, out double c) ? (float)c : PixaiTaggerAPI.DefaultCharacterThreshold;
        clothThresh = input.TryGet(ClothingThresholdParam, out double cl) ? (float)cl : PixaiTaggerAPI.DefaultClothingThreshold;

        if (string.IsNullOrWhiteSpace(data))
        {
            return;
        }

        string[] parts = data.Split([','], StringSplitOptions.TrimEntries);
        if (parts.Length > 0 && float.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out float parsedG))
        {
            genThresh = Math.Clamp(parsedG, 0f, 1f);
        }
        if (parts.Length > 1 && float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float parsedC))
        {
            charThresh = Math.Clamp(parsedC, 0f, 1f);
        }
        if (parts.Length > 2 && float.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out float parsedCl))
        {
            clothThresh = Math.Clamp(parsedCl, 0f, 1f);
        }
    }

    /// <summary>Expands the &lt;pixaitagger&gt; prompt tag into tags from the init image.</summary>
    private static string GeneratePromptTagTags(string data, T2IPromptHandling.PromptTagContext context)
    {
        if (context?.Input is null)
        {
            return "";
        }

        ResolvePromptTagThresholds(data, context.Input, out float genThresh, out float charThresh, out float clothThresh);
        string filterTags = context.Input.Get(FilterTagsParam, "");
        bool includeConf = context.Input.Get(IncludeConfidenceParam, false);
        bool keepUnder = context.Input.Get(KeepUnderscoresParam, false);

        string cacheKey = $"{genThresh}|{charThresh}|{clothThresh}|{includeConf}|{keepUnder}|{filterTags}";
        Dictionary<string, string> cache = context.Input.ExtraMeta.GetOrCreate(PromptTagCacheKey, () => new Dictionary<string, string>()) as Dictionary<string, string>;
        if (cache.TryGetValue(cacheKey, out string cached))
        {
            return cached;
        }

        Image source = GetPromptTagImageSource(context.Input);
        if (source is null)
        {
            context.TrackWarning("PixAI Tagger: Prompt tag '<pixaitagger>' used, but no init image or prompt image was provided.");
            cache[cacheKey] = "";
            return "";
        }

        try
        {
            JObject result = PixaiTaggerAPI.PixaiTaggerGenerateTags(
                context.Input.SourceSession,
                source.AsBase64,
                generalThreshold: genThresh,
                characterThreshold: charThresh,
                clothingThreshold: clothThresh,
                includeConfidence: includeConf,
                keepUnderscores: keepUnder,
                filterTags: filterTags
            ).GetAwaiter().GetResult();

            if (result?["success"]?.Value<bool>() != true)
            {
                string err = result?["error"]?.Value<string>() ?? "Unknown PixAI error.";
                context.TrackWarning($"PixAI Tagger prompt tag failed: {err}");
                cache[cacheKey] = "";
                return "";
            }

            string tags = result?["tags"]?.Value<string>() ?? "";
            cache[cacheKey] = tags;
            return tags;
        }
        catch (Exception ex)
        {
            context.TrackWarning($"PixAI Tagger prompt tag exception: {ex.Message}");
            cache[cacheKey] = "";
            return "";
        }
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

        GeneralThresholdParam = T2IParamTypes.Register<double>(new(
            Name: "[PixAI] General Threshold",
            Description: "Minimum confidence threshold for general tags (actions, environment, objects). Default: 0.17",
            Default: "0.17",
            Min: 0.0,
            Max: 1.0,
            Step: 0.01,
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
            Group: PixaiTaggerGroup,
            OrderPriority: 2
        ));

        StyleThresholdParam = T2IParamTypes.Register<double>(new(
            Name: "[PixAI] Style Threshold",
            Description: "Minimum confidence threshold for art style and aesthetic tags. Default: 0.15",
            Default: "0.15",
            Min: 0.0,
            Max: 1.0,
            Step: 0.01,
            Group: PixaiTaggerGroup,
            OrderPriority: 3
        ));

        CopyrightThresholdParam = T2IParamTypes.Register<double>(new(
            Name: "[PixAI] Copyright / Series Threshold",
            Description: "Minimum confidence threshold for series, franchise, and work IP tags. Default: 0.24",
            Default: "0.24",
            Min: 0.0,
            Max: 1.0,
            Step: 0.01,
            Group: PixaiTaggerGroup,
            OrderPriority: 4
        ));

        ClothingThresholdParam = T2IParamTypes.Register<double>(new(
            Name: "[PixAI] Clothing Threshold",
            Description: "Minimum confidence threshold for clothing, garments, and attire tags. Default: 0.17",
            Default: "0.17",
            Min: 0.0,
            Max: 1.0,
            Step: 0.01,
            Group: PixaiTaggerGroup,
            OrderPriority: 5
        ));

        MetaThresholdParam = T2IParamTypes.Register<double>(new(
            Name: "[PixAI] Meta Threshold",
            Description: "Minimum confidence threshold for metadata tags (highres, official art, etc.). Default: 0.17",
            Default: "0.17",
            Min: 0.0,
            Max: 1.0,
            Step: 0.01,
            Group: PixaiTaggerGroup,
            OrderPriority: 6
        ));

        RatingThresholdParam = T2IParamTypes.Register<double>(new(
            Name: "[PixAI] Rating Threshold",
            Description: "Minimum confidence threshold for content rating tags (rating:general, rating:sensitive, rating:questionable, rating:explicit). Default: 0.41",
            Default: "0.41",
            Min: 0.0,
            Max: 1.0,
            Step: 0.01,
            Group: PixaiTaggerGroup,
            OrderPriority: 7
        ));

        IncludeConfidenceParam = T2IParamTypes.Register<bool>(new(
            Name: "[PixAI] Include Confidence Scores",
            Description: "Appends confidence scores to tags (e.g. tag:0.95).",
            Default: "false",
            Group: PixaiTaggerGroup,
            OrderPriority: 8
        ));

        KeepUnderscoresParam = T2IParamTypes.Register<bool>(new(
            Name: "[PixAI] Keep Underscores",
            Description: "Preserves underscores in tag names (e.g. school_uniform) instead of converting them to spaces.",
            Default: "false",
            Group: PixaiTaggerGroup,
            OrderPriority: 9
        ));

        FilterTagsParam = T2IParamTypes.Register<string>(new(
            Name: "[PixAI] Filter Tags",
            Description: "Comma-separated list of tags to exclude or replace. Format: 'tag_to_exclude', or 'original:replacement'. Supports wildcard patterns like 'hair*' or '*skirt'.",
            Default: "",
            Group: PixaiTaggerGroup,
            OrderPriority: 10
        ));

        InsertModeParam = T2IParamTypes.Register<string>(new(
            Name: "[PixAI] Insert Mode",
            Description: "How generated tags are inserted into the prompt box.",
            Default: "replace",
            GetValues: _ => ["replace///Replace prompt", "prepend///Prepend to prompt", "append///Append to prompt"],
            Group: PixaiTaggerGroup,
            OrderPriority: 11
        ));

        T2IPromptHandling.PromptTagProcessors["pixaitagger"] = GeneratePromptTagTags;
    }

    public override void OnInit()
    {
        PixaiTaggerAPI.Register();
    }
}
