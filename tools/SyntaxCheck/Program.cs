using System;
using System.IO;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

var root = args.Length > 0
    ? Path.GetFullPath(args[0])
    : Directory.GetCurrentDirectory();

var files = Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
    .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
    .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
    .OrderBy(path => path, StringComparer.Ordinal)
    .ToList();

var hadErrors = false;

foreach (var file in files)
{
    var source = await File.ReadAllTextAsync(file);
    var tree = CSharpSyntaxTree.ParseText(source, path: file);
    var diagnostics = tree.GetDiagnostics()
        .Where(d => d.Severity == DiagnosticSeverity.Error)
        .OrderBy(d => d.Location.GetLineSpan().StartLinePosition.Line)
        .ToList();

    if (diagnostics.Count == 0)
        continue;

    hadErrors = true;

    foreach (var diagnostic in diagnostics)
    {
        var span = diagnostic.Location.GetLineSpan();
        var line = span.StartLinePosition.Line + 1;
        var column = span.StartLinePosition.Character + 1;
        Console.WriteLine($"{file}:{line}:{column}: {diagnostic.Id} {diagnostic.GetMessage()}");
    }
}

return hadErrors ? 1 : 0;
