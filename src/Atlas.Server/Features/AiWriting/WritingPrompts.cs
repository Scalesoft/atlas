namespace Atlas.Server.Features.AiWriting;

/// <summary>Version 1 working prompts; content is a delimited input and never a model instruction.</summary>
public static class WritingPrompts
{
    public static string For(string task)
    {
        var resource = "Atlas.Server.Features.AiWriting.Prompts." + task + ".system.v1.txt";
        using var stream = typeof(WritingPrompts).Assembly.GetManifestResourceStream(resource)
            ?? throw new InvalidOperationException("Writing prompt is missing.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd().ReplaceLineEndings("\n").TrimEnd();
    }
}
