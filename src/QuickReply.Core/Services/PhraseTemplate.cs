using System.Text.RegularExpressions;

namespace QuickReply.Core.Services;

/// <summary>A variable in a phrase: <c>{单号}</c> has no default, <c>{天数=3}</c> defaults to 3.</summary>
public sealed record PhraseVariable(string Name, string DefaultValue, bool HasDefault);

/// <summary>
/// Phrase variables. The agent fills them in before the phrase is sent; an empty value falls back
/// to the default written in the phrase. A name repeated in one phrase is asked for once.
/// </summary>
public static class PhraseTemplate
{
    // Names exclude quotes, colons and commas so JSON-like text such as {"a":1} is left alone.
    private static readonly Regex VariableRegex = new(
        @"\{(?<name>[^{}=\r\n""'：:，,]{1,20})(?:=(?<default>[^{}\r\n]{0,200}))?\}",
        RegexOptions.Compiled);

    public static bool HasVariables(string? content)
    {
        return Parse(content).Count > 0;
    }

    public static IReadOnlyList<PhraseVariable> Parse(string? content)
    {
        if (string.IsNullOrEmpty(content) || content.IndexOf('{') < 0)
        {
            return [];
        }

        var variables = new List<PhraseVariable>();
        foreach (Match match in VariableRegex.Matches(content))
        {
            var name = match.Groups["name"].Value.Trim();
            if (name.Length == 0 || variables.Any(variable => variable.Name == name))
            {
                continue;
            }

            var defaultGroup = match.Groups["default"];
            variables.Add(new PhraseVariable(name, defaultGroup.Success ? defaultGroup.Value : string.Empty, defaultGroup.Success));
        }

        return variables;
    }

    /// <summary>Variables that still have no value: no default, and nothing (or only spaces) entered.</summary>
    public static IReadOnlyList<PhraseVariable> FindMissing(
        IReadOnlyList<PhraseVariable> variables,
        IReadOnlyDictionary<string, string> values)
    {
        return variables
            .Where(variable => !variable.HasDefault &&
                               (!values.TryGetValue(variable.Name, out var value) || string.IsNullOrWhiteSpace(value)))
            .ToList();
    }

    public static string Fill(string content, IReadOnlyDictionary<string, string> values)
    {
        if (string.IsNullOrEmpty(content))
        {
            return content;
        }

        // Repeats use the first occurrence's default, the same one the agent was shown.
        var variables = Parse(content).ToDictionary(variable => variable.Name);
        return VariableRegex.Replace(content, match =>
        {
            var name = match.Groups["name"].Value.Trim();
            if (!variables.TryGetValue(name, out var variable))
            {
                return match.Value;
            }

            if (values.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value))
            {
                return value;
            }

            return variable.HasDefault ? variable.DefaultValue : match.Value;
        });
    }
}
