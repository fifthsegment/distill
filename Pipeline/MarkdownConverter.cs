using ReverseMarkdown;

namespace Distill.Pipeline;

public static class MarkdownConverter
{
    private static readonly Converter Converter = new(new Config
    {
        GithubFlavored = true,
        RemoveComments = true,
        SmartHrefHandling = true,
        UnknownTags = Config.UnknownTagsOption.Bypass,
    });

    public static string Convert(string html)
    {
        var markdown = Converter.Convert(html);
        return CollapseBlankLines(markdown).Trim();
    }

    private static string CollapseBlankLines(string text)
    {
        var lines = text.Split('\n');
        var result = new List<string>();
        int consecutive = 0;

        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                consecutive++;
                if (consecutive <= 2)
                    result.Add("");
            }
            else
            {
                consecutive = 0;
                result.Add(line);
            }
        }

        return string.Join('\n', result);
    }
}
