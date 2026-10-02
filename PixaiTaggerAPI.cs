using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using FreneticUtilities.FreneticExtensions;
using Newtonsoft.Json.Linq;
using SwarmUI.Accounts;
using SwarmUI.Backends;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Core;
using SwarmUI.Text2Image;
using SwarmUI.Utils;
using SwarmUI.WebAPI;

namespace SwarmUI.PixaiTagger;

/// <summary>Permission definitions for the PixAI Tagger extension.</summary>
public static class PixaiTaggerPermissions
{
    /// <summary>Permission group for PixAITagger.</summary>
    public static readonly PermInfoGroup PixaiTaggerPermGroup = new("PixAITagger", "Permissions related to PixAI Tagger functionality.");

    /// <summary>Permission to call the tag-generation API.</summary>
    public static readonly PermInfo PermGenerateTags = Permissions.Register(new(
        "pixaitagger_generate_tags",
        "Generate PixAI Tags",
        "Allows the user to run PixAI tag generation on images using GPU.",
        PermissionDefault.USER,
        PixaiTaggerPermGroup));
}

/// <summary>API routes for the PixAI Tagger extension.</summary>
[API.APIClass("API routes related to PixAI Tagger extension")]
public static class PixaiTaggerAPI
{
    /// <summary>Default confidence threshold for general tags.</summary>
    public const float DefaultGeneralThreshold = 0.17f;

    /// <summary>Default confidence threshold for character tags.</summary>
    public const float DefaultCharacterThreshold = 0.27f;

    /// <summary>Default confidence threshold for style tags.</summary>
    public const float DefaultStyleThreshold = 0.15f;

    /// <summary>Default confidence threshold for copyright tags.</summary>
    public const float DefaultCopyrightThreshold = 0.24f;

    /// <summary>Default confidence threshold for clothing tags.</summary>
    public const float DefaultClothingThreshold = 0.17f;

    /// <summary>Matches a trailing prompt-weight suffix like ":1.3" on a tag's core text.</summary>
    private static readonly Regex TrailingWeightPattern = new(@":\s*\d+(?:\.\d+)?\s*$", RegexOptions.Compiled);

    /// <summary>Maximum allowed byte length for the filterTags string.</summary>
    private const int MaxFilterTagsLength = 4096;

    /// <summary>Registers all API calls for this extension.</summary>
    public static void Register()
    {
        API.RegisterAPICall(PixaiTaggerGenerateTags, true, PixaiTaggerPermissions.PermGenerateTags);
        API.RegisterAPICall(PixaiTaggerApplyFilters, true, PixaiTaggerPermissions.PermGenerateTags);
    }

    /// <summary>Supported matching styles for filter rule source tags.</summary>
    private enum FilterTagMatchMode
    {
        Exact,
        StartsWithPhrase,
        EndsWithPhrase,
        ContainsPhrase
    }

    /// <summary>A single parsed wildcard rule.</summary>
    private record FilterTagRule(FilterTagMatchMode MatchMode, string SourceTag, string TargetTag);

    /// <summary>Parsed filter settings grouped by precedence.</summary>
    private record FilterTagRules(
        HashSet<string> ExactExcludedTags,
        Dictionary<string, string> ExactReplacementTags,
        List<FilterTagRule> WildcardExclusionRules,
        List<FilterTagRule> WildcardReplacementRules);

    /// <summary>Normalizes whitespace so matching treats repeated spaces consistently.</summary>
    private static string NormalizeTagText(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return "";
        }
        return string.Join(' ', text.Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    /// <summary>Returns whether the character should count as part of a word for boundary checks.</summary>
    private static bool IsWordCharacter(char character)
    {
        return char.IsLetterOrDigit(character);
    }

    /// <summary>Checks whether a candidate phrase match starts and ends on non-word boundaries.</summary>
    private static bool IsPhraseBoundaryMatch(string candidateTag, int startIndex, int length)
    {
        int endIndex = startIndex + length;
        bool hasLeadingBoundary = startIndex <= 0 || !IsWordCharacter(candidateTag[startIndex - 1]);
        bool hasTrailingBoundary = endIndex >= candidateTag.Length || !IsWordCharacter(candidateTag[endIndex]);
        return hasLeadingBoundary && hasTrailingBoundary;
    }

    /// <summary>Finds a boundary-aware phrase match anywhere within a candidate tag.</summary>
    private static bool ContainsPhraseBoundaryMatch(string candidateTag, string sourceTag)
    {
        int searchIndex = 0;
        while (searchIndex < candidateTag.Length)
        {
            int matchIndex = candidateTag.IndexOf(sourceTag, searchIndex, StringComparison.OrdinalIgnoreCase);
            if (matchIndex < 0)
            {
                return false;
            }
            if (IsPhraseBoundaryMatch(candidateTag, matchIndex, sourceTag.Length))
            {
                return true;
            }
            searchIndex = matchIndex + 1;
        }
        return false;
    }

    /// <summary>Parses a rule source token into an exact or wildcard match mode.</summary>
    private static FilterTagMatchMode ParseFilterTagMatchMode(string rawSourceTag, out string normalizedSourceTag)
    {
        string sourceTag = NormalizeTagText(rawSourceTag);
        bool hasLeadingWildcard = sourceTag.StartsWith('*');
        bool hasTrailingWildcard = sourceTag.EndsWith('*');
        string coreTag = sourceTag.Trim('*');
        if (string.IsNullOrWhiteSpace(coreTag) || coreTag.Contains('*'))
        {
            normalizedSourceTag = sourceTag;
            return FilterTagMatchMode.Exact;
        }
        normalizedSourceTag = NormalizeTagText(coreTag);
        if (hasLeadingWildcard && hasTrailingWildcard)
        {
            return FilterTagMatchMode.ContainsPhrase;
        }
        if (hasLeadingWildcard)
        {
            return FilterTagMatchMode.EndsWithPhrase;
        }
        if (hasTrailingWildcard)
        {
            return FilterTagMatchMode.StartsWithPhrase;
        }
        return FilterTagMatchMode.Exact;
    }

    /// <summary>Checks whether a tag matches an exact or boundary-aware phrase pattern.</summary>
    private static bool MatchesFilterRule(string candidateTag, string sourceTag, FilterTagMatchMode matchMode)
    {
        string normalizedCandidate = NormalizeTagText(candidateTag);
        if (string.IsNullOrWhiteSpace(normalizedCandidate) || string.IsNullOrWhiteSpace(sourceTag))
        {
            return false;
        }
        if (matchMode == FilterTagMatchMode.Exact)
        {
            return normalizedCandidate.Equals(sourceTag, StringComparison.OrdinalIgnoreCase);
        }
        if (matchMode == FilterTagMatchMode.StartsWithPhrase)
        {
            return normalizedCandidate.StartsWith(sourceTag, StringComparison.OrdinalIgnoreCase)
                && IsPhraseBoundaryMatch(normalizedCandidate, 0, sourceTag.Length);
        }
        if (matchMode == FilterTagMatchMode.EndsWithPhrase)
        {
            int startIndex = normalizedCandidate.Length - sourceTag.Length;
            return startIndex >= 0
                && normalizedCandidate.EndsWith(sourceTag, StringComparison.OrdinalIgnoreCase)
                && IsPhraseBoundaryMatch(normalizedCandidate, startIndex, sourceTag.Length);
        }
        return ContainsPhraseBoundaryMatch(normalizedCandidate, sourceTag);
    }

    /// <summary>Applies a wildcard replacement to a tag, substituting only the matched portion.</summary>
    private static string ApplyWildcardReplacement(string candidateTag, FilterTagRule rule)
    {
        string normalizedCandidate = NormalizeTagText(candidateTag);
        if (rule.MatchMode == FilterTagMatchMode.StartsWithPhrase)
        {
            if (normalizedCandidate.StartsWith(rule.SourceTag, StringComparison.OrdinalIgnoreCase)
                && IsPhraseBoundaryMatch(normalizedCandidate, 0, rule.SourceTag.Length))
            {
                return rule.TargetTag + normalizedCandidate[rule.SourceTag.Length..];
            }
            return normalizedCandidate;
        }
        if (rule.MatchMode == FilterTagMatchMode.EndsWithPhrase)
        {
            int startIndex = normalizedCandidate.Length - rule.SourceTag.Length;
            if (startIndex >= 0
                && normalizedCandidate.EndsWith(rule.SourceTag, StringComparison.OrdinalIgnoreCase)
                && IsPhraseBoundaryMatch(normalizedCandidate, startIndex, rule.SourceTag.Length))
            {
                return normalizedCandidate[..startIndex] + rule.TargetTag;
            }
            return normalizedCandidate;
        }
        if (rule.MatchMode == FilterTagMatchMode.ContainsPhrase)
        {
            int searchIndex = 0;
            StringBuilder builder = new();
            while (searchIndex < normalizedCandidate.Length)
            {
                int matchIndex = normalizedCandidate.IndexOf(rule.SourceTag, searchIndex, StringComparison.OrdinalIgnoreCase);
                if (matchIndex < 0)
                {
                    builder.Append(normalizedCandidate[searchIndex..]);
                    break;
                }
                if (!IsPhraseBoundaryMatch(normalizedCandidate, matchIndex, rule.SourceTag.Length))
                {
                    builder.Append(normalizedCandidate[searchIndex..(matchIndex + 1)]);
                    searchIndex = matchIndex + 1;
                    continue;
                }
                builder.Append(normalizedCandidate[searchIndex..matchIndex]);
                builder.Append(rule.TargetTag);
                searchIndex = matchIndex + rule.SourceTag.Length;
            }
            return builder.ToString();
        }
        return normalizedCandidate;
    }

    /// <summary>Parses a comma-separated filter string into exclusion and substitution rules.</summary>
    private static FilterTagRules ParseFilterTagRules(string filterTags)
    {
        HashSet<string> exactExcludedTags = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, string> exactReplacementTags = new(StringComparer.OrdinalIgnoreCase);
        List<FilterTagRule> wildcardExclusionRules = [];
        List<FilterTagRule> wildcardReplacementRules = [];

        if (string.IsNullOrWhiteSpace(filterTags))
        {
            return new FilterTagRules(exactExcludedTags, exactReplacementTags, wildcardExclusionRules, wildcardReplacementRules);
        }

        string[] entries = filterTags.Split([',', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (string rawEntry in entries)
        {
            string entry = rawEntry.Trim();
            if (string.IsNullOrWhiteSpace(entry))
            {
                continue;
            }
            int separatorIndex = entry.IndexOf(':');
            if (separatorIndex > 0 && separatorIndex < entry.Length - 1)
            {
                string sourceTag = entry[..separatorIndex].Trim().Trim('"');
                string targetTag = NormalizeTagText(entry[(separatorIndex + 1)..].Trim().Trim('"'));
                if (!string.IsNullOrWhiteSpace(sourceTag) && !string.IsNullOrWhiteSpace(targetTag))
                {
                    FilterTagMatchMode matchMode = ParseFilterTagMatchMode(sourceTag, out string normalizedSourceTag);
                    if (matchMode == FilterTagMatchMode.Exact)
                    {
                        exactReplacementTags[normalizedSourceTag] = targetTag;
                    }
                    else
                    {
                        wildcardReplacementRules.Add(new FilterTagRule(matchMode, normalizedSourceTag, targetTag));
                    }
                    continue;
                }
            }
            FilterTagMatchMode exMatchMode = ParseFilterTagMatchMode(entry.Trim('"'), out string normExTag);
            if (exMatchMode == FilterTagMatchMode.Exact)
            {
                exactExcludedTags.Add(normExTag);
            }
            else
            {
                wildcardExclusionRules.Add(new FilterTagRule(exMatchMode, normExTag, ""));
            }
        }
        return new FilterTagRules(exactExcludedTags, exactReplacementTags, wildcardExclusionRules, wildcardReplacementRules);
    }

    /// <summary>Splits prompt weighting decorations from a tag core.</summary>
    private static (string prefix, string core, string suffix) SplitPromptWeighting(string rawTag)
    {
        string working = rawTag.Trim();
        int leadingParens = 0;
        int startIndex = 0;
        while (startIndex < working.Length && working[startIndex] == '(')
        {
            leadingParens++;
            startIndex++;
        }
        int endIndex = working.Length;
        int trailingParens = 0;
        while (endIndex > startIndex && working[endIndex - 1] == ')')
        {
            trailingParens++;
            endIndex--;
        }
        if (leadingParens != trailingParens)
        {
            leadingParens = 0;
            trailingParens = 0;
            startIndex = 0;
            endIndex = working.Length;
        }
        string core = working[startIndex..endIndex];
        string weight = "";
        Match weightMatch = TrailingWeightPattern.Match(core);
        if (weightMatch.Success)
        {
            weight = core[weightMatch.Index..].Trim();
            core = core[..weightMatch.Index];
        }
        string prefix = new('(', leadingParens);
        string suffix = weight + new string(')', trailingParens);
        return (prefix, core, suffix);
    }

    /// <summary>Applies exact replacements, exact exclusions, wildcard replacements, and wildcard exclusions.</summary>
    private static string ApplyFilterTagRules(string rawTags, FilterTagRules rules)
    {
        if (string.IsNullOrWhiteSpace(rawTags))
        {
            return "";
        }
        List<string> updatedTags = [];
        foreach (string rawTag in rawTags.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            (string prefix, string coreRaw, string suffix) = SplitPromptWeighting(rawTag);
            string tag = NormalizeTagText(coreRaw);
            if (string.IsNullOrWhiteSpace(tag))
            {
                continue;
            }
            if (rules.ExactReplacementTags.TryGetValue(tag, out string exactReplacement))
            {
                tag = exactReplacement;
            }
            if (rules.ExactExcludedTags.Contains(tag))
            {
                continue;
            }
            foreach (FilterTagRule wildcardReplacementRule in rules.WildcardReplacementRules)
            {
                if (MatchesFilterRule(tag, wildcardReplacementRule.SourceTag, wildcardReplacementRule.MatchMode))
                {
                    tag = ApplyWildcardReplacement(tag, wildcardReplacementRule);
                    break;
                }
            }
            bool isWildcardExcluded = false;
            foreach (FilterTagRule wildcardExclusionRule in rules.WildcardExclusionRules)
            {
                if (MatchesFilterRule(tag, wildcardExclusionRule.SourceTag, wildcardExclusionRule.MatchMode))
                {
                    isWildcardExcluded = true;
                    break;
                }
            }
            if (!isWildcardExcluded)
            {
                updatedTags.Add(prefix + tag + suffix);
            }
        }
        return string.Join(", ", updatedTags);
    }

    /// <summary>Generates PixAI tags for the provided image using GPU acceleration.</summary>
    public static async Task<JObject> PixaiTaggerGenerateTags(
        Session session,
        string imageBase64,
        float generalThreshold = DefaultGeneralThreshold,
        float characterThreshold = DefaultCharacterThreshold,
        float clothingThreshold = DefaultClothingThreshold,
        float styleThreshold = DefaultStyleThreshold,
        float copyrightThreshold = DefaultCopyrightThreshold,
        bool enableGeneral = true,
        bool enableCharacter = true,
        bool enableClothing = true,
        bool enableStyle = false,
        bool enableCopyright = false,
        bool includeConfidence = false,
        bool keepUnderscores = false,
        string filterTags = "")
    {
        if (string.IsNullOrWhiteSpace(imageBase64))
        {
            return new JObject { ["success"] = false, ["error"] = "No image data provided." };
        }

        // Strip data:image/...;base64, header if present
        if (imageBase64.StartsWith("data:"))
        {
            int commaIdx = imageBase64.IndexOf(',');
            if (commaIdx >= 0)
            {
                imageBase64 = imageBase64[(commaIdx + 1)..];
            }
        }

        // Check for local ComfyUI backend
        ComfyUIAPIAbstractBackend backend = ComfyUIBackendExtension.RunningComfyBackends.FirstOrDefault(b => b is ComfyUISelfStartBackend)
            ?? ComfyUIBackendExtension.RunningComfyBackends.FirstOrDefault();

        // If no local ComfyUI backend is available, forward request to remote SwarmUI instance if connected via Swarm-to-Swarm API backend
        if (backend is null)
        {
            SwarmSwarmBackend remoteBackend = Program.Backends.RunningBackendsOfType<SwarmSwarmBackend>()
                .Where(s => s.LinkedRemoteBackendType is not null && s.LinkedRemoteBackendType.StartsWith("comfyui_")).FirstOrDefault()
                ?? Program.Backends.RunningBackendsOfType<SwarmSwarmBackend>().FirstOrDefault(s => s.IsAControlInstance)
                ?? Program.Backends.RunningBackendsOfType<SwarmSwarmBackend>().FirstOrDefault();

            if (remoteBackend is not null)
            {
                Logs.Info($"[PixAITagger] No local ComfyUI backend found. Forwarding PixAI Tagger request to remote SwarmUI backend at {remoteBackend.Address}...");
                JObject forwardReq = new()
                {
                    ["imageBase64"] = imageBase64,
                    ["generalThreshold"] = generalThreshold,
                    ["characterThreshold"] = characterThreshold,
                    ["clothingThreshold"] = clothingThreshold,
                    ["styleThreshold"] = styleThreshold,
                    ["copyrightThreshold"] = copyrightThreshold,
                    ["enableGeneral"] = enableGeneral,
                    ["enableCharacter"] = enableCharacter,
                    ["enableClothing"] = enableClothing,
                    ["enableStyle"] = enableStyle,
                    ["enableCopyright"] = enableCopyright,
                    ["includeConfidence"] = includeConfidence,
                    ["keepUnderscores"] = keepUnderscores,
                    ["filterTags"] = filterTags
                };

                try
                {
                    using Session.GenClaim claim = session.Claim(liveGens: 1);
                    return await remoteBackend.SendAPIJSON("PixaiTaggerGenerateTags", forwardReq);
                }
                catch (Exception ex)
                {
                    Logs.Error($"[PixAITagger] Failed to forward tagging request to remote SwarmUI ({remoteBackend.Address}): {ex.Message}");
                    if (ex.Message.Contains("PixaiTaggerGenerateTags") || ex.Message.Contains("Unknown API call"))
                    {
                        return new JObject
                        {
                            ["success"] = false,
                            ["error"] = $"PixAI Tagger failed: The remote SwarmUI instance at {remoteBackend.Address} does not have the SwarmUI-PixaiTagger extension installed. Please install SwarmUI-PixaiTagger on the remote machine."
                        };
                    }
                    return new JObject
                    {
                        ["success"] = false,
                        ["error"] = $"PixAI Tagger remote execution failed: {ex.Message}"
                    };
                }
            }

            throw new SwarmUserErrorException("No available ComfyUI or remote SwarmUI Backend to run this operation");
        }

        string tempOutputPath = Path.Combine(Path.GetTempPath(), $"pixaitagger_{Guid.NewGuid():N}.json").Replace('\\', '/');

        string hfHubCache = Environment.GetEnvironmentVariable("HF_HUB_CACHE");
        string hfHome = Environment.GetEnvironmentVariable("HF_HOME");
        string modelCacheDir = null;
        if (!string.IsNullOrWhiteSpace(hfHubCache))
        {
            modelCacheDir = Path.Combine(hfHubCache, "models--pixai-labs--pixai-tagger-v1.0");
        }
        else if (!string.IsNullOrWhiteSpace(hfHome))
        {
            modelCacheDir = Path.Combine(hfHome, "hub", "models--pixai-labs--pixai-tagger-v1.0");
        }
        else
        {
            string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            modelCacheDir = Path.Combine(userProfile, ".cache", "huggingface", "hub", "models--pixai-labs--pixai-tagger-v1.0");
        }

        bool isModelCached = !string.IsNullOrWhiteSpace(modelCacheDir) && Directory.Exists(modelCacheDir) && Directory.EnumerateFiles(modelCacheDir, "*", SearchOption.AllDirectories).Any();
        if (!isModelCached)
        {
            Logs.Init("[PixAITagger] Model 'pixai-labs/pixai-tagger-v1.0' not found in local Hugging Face cache. Downloading model weights from Hugging Face (first-time setup)...");
        }
        else
        {
            Logs.Info("[PixAITagger] PixAI Tagger v1.0 model weights verified in local cache.");
        }

        long startTime = Environment.TickCount64;

        try
        {
            JObject workflow = new()
            {
                ["1"] = new JObject
                {
                    ["class_type"] = "SwarmLoadImageB64",
                    ["inputs"] = new JObject
                    {
                        ["image_base64"] = imageBase64
                    }
                },
                ["2"] = new JObject
                {
                    ["class_type"] = "PixaiTaggerGenerate",
                    ["inputs"] = new JObject
                    {
                        ["images"] = new JArray { "1", 0 },
                        ["general_threshold"] = generalThreshold,
                        ["character_threshold"] = characterThreshold,
                        ["clothing_threshold"] = clothingThreshold,
                        ["style_threshold"] = styleThreshold,
                        ["copyright_threshold"] = copyrightThreshold,
                        ["enable_general"] = enableGeneral,
                        ["enable_character"] = enableCharacter,
                        ["enable_clothing"] = enableClothing,
                        ["enable_style"] = enableStyle,
                        ["enable_copyright"] = enableCopyright,
                        ["include_confidence"] = includeConfidence,
                        ["keep_underscores"] = keepUnderscores,
                        ["exclude_tags"] = "",
                        ["output_path"] = tempOutputPath
                    }
                }
            };

            Logs.Info("[PixAITagger] Sending image tagging task to ComfyUI GPU backend...");

            T2IParamInput customInput = new(session);

            using Session.GenClaim claim = session.Claim(liveGens: 1);
            await backend.AwaitJobLive(workflow.ToString(), "0", _ => { }, customInput, Program.GlobalProgramCancel);

            if (!File.Exists(tempOutputPath))
            {
                return new JObject
                {
                    ["success"] = false,
                    ["error"] = "The PixAI Tagger workflow completed, but no tag output was received. Please ensure a ComfyUI self-start backend is running."
                };
            }

            string jsonContent = await File.ReadAllTextAsync(tempOutputPath);
            JObject parsedResult = JObject.Parse(jsonContent);

            FilterTagRules filterRules = ParseFilterTagRules(filterTags);

            string combined = parsedResult["combined_tags"]?.ToString() ?? "";
            string character = parsedResult["character"]?.ToString() ?? "";
            string copyright = parsedResult["copyright"]?.ToString() ?? "";
            string style = parsedResult["style"]?.ToString() ?? "";
            string clothing = parsedResult["clothing"]?.ToString() ?? "";
            string general = parsedResult["general"]?.ToString() ?? "";

            if (!string.IsNullOrWhiteSpace(filterTags))
            {
                combined = ApplyFilterTagRules(combined, filterRules);
                character = ApplyFilterTagRules(character, filterRules);
                copyright = ApplyFilterTagRules(copyright, filterRules);
                style = ApplyFilterTagRules(style, filterRules);
                clothing = ApplyFilterTagRules(clothing, filterRules);
                general = ApplyFilterTagRules(general, filterRules);
            }

            long elapsedMs = Environment.TickCount64 - startTime;
            double elapsedSec = elapsedMs / 1000.0;

            if (!isModelCached && !string.IsNullOrWhiteSpace(modelCacheDir) && Directory.Exists(modelCacheDir) && Directory.EnumerateFiles(modelCacheDir, "*", SearchOption.AllDirectories).Any())
            {
                Logs.Init($"[PixAITagger] Model 'pixai-labs/pixai-tagger-v1.0' downloaded and cached successfully to: {modelCacheDir}");
            }

            int tagCount = combined.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Length;
            Logs.Info($"[PixAITagger] Tag generation completed in {elapsedSec:0.00}s. Extracted {tagCount} tags.");

            return new JObject
            {
                ["success"] = true,
                ["tags"] = combined,
                ["character"] = character,
                ["copyright"] = copyright,
                ["style"] = style,
                ["clothing"] = clothing,
                ["general"] = general,
                ["details"] = parsedResult["details"]
            };
        }
        catch (Exception ex)
        {
            Logs.Error($"PixAITagger error during tag generation: {ex}");
            return new JObject
            {
                ["success"] = false,
                ["error"] = $"PixAI Tagger failed: {ex.Message}"
            };
        }
        finally
        {
            if (File.Exists(tempOutputPath))
            {
                try
                {
                    File.Delete(tempOutputPath);
                }
                catch
                {
                }
            }
        }
    }

    /// <summary>Applies tag filter replacement and exclusion rules to an existing tag string.</summary>
    public static async Task<JObject> PixaiTaggerApplyFilters(Session session, string tags, string filterTags)
    {
        await Task.CompletedTask;
        if (string.IsNullOrWhiteSpace(tags))
        {
            return new JObject { ["success"] = true, ["tags"] = "" };
        }
        FilterTagRules rules = ParseFilterTagRules(filterTags);
        string filtered = ApplyFilterTagRules(tags, rules);
        return new JObject { ["success"] = true, ["tags"] = filtered };
    }
}
