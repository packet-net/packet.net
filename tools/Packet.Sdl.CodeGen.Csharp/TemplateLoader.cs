using System.Reflection;
using Scriban;
using Scriban.Runtime;

namespace Packet.Sdl.CodeGen.Csharp;

/// <summary>
/// Loads Scriban templates from this assembly's embedded resources and
/// renders C# view-models through them with snake_case member access
/// (templates use <c>page.class_name</c>, <c>t.actions_csv</c>, etc.).
/// </summary>
internal static class TemplateLoader
{
    public static Template Load(string name)
    {
        var asm = typeof(TemplateLoader).Assembly;
        var resourceName = $"Packet.Sdl.CodeGen.Csharp.Templates.{name}";
        using var stream = asm.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException(
                $"missing embedded template '{resourceName}'. Resources present: {string.Join(", ", asm.GetManifestResourceNames())}");
        using var reader = new StreamReader(stream);
        var text = reader.ReadToEnd();
        var template = Template.Parse(text, sourceFilePath: name);
        if (template.HasErrors)
        {
            var msgs = string.Join("; ", template.Messages.Select(m => m.ToString()));
            throw new InvalidOperationException($"template '{name}' parse errors: {msgs}");
        }
        return template;
    }

    public static string Render<T>(Template template, T model)
        => template.Render(new { page = model }, member => StandardMemberRenamer.Default(member));
}
