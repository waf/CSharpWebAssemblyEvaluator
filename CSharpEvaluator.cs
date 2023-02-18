using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Scripting.Hosting;

namespace CSharpWebAssemblyEvaluator;

public record Result(IReadOnlyCollection<string> Output, IReadOnlyCollection<string> Error);

public class CSharpEvaluator
{
    // dependencies
    private readonly HttpClient http;

    // state
    private MetadataReference[] _references;
    private string[] _usings;

    private CSharpCompilation _previousCompilation;
    private object[] _submissionStates = new object[] { null, null };
    private int _submissionIndex = 0;

    public CSharpEvaluator(HttpClient http)
    {
        this.http = http;
    }

    public async Task DownloadAssemblyReferencesAsync(
        string[] assemblyNames = null,
        string[] usings = null
    )
    {
        assemblyNames ??= AppDomain.CurrentDomain
            .GetAssemblies()
            .Append(typeof(Console).Assembly)
            .Select(assembly => assembly.GetName().Name)
            .ToArray();

        this._references = await Task.WhenAll(
            assemblyNames.Select(
                async assemblyName =>
                    MetadataReference.CreateFromStream(
                        await http.GetStreamAsync($"_framework/{assemblyName}.dll")
                    )
            )
        );
        this._usings =
            usings
            ?? new[]
            {
                "System",
                "System.IO",
                "System.Collections.Generic",
                "System.Diagnostics",
                "System.Linq",
                "System.Net.Http",
                "System.Text",
                "System.Threading.Tasks"
            };
    }

    public async Task<Result> Run(string code)
    {
        var stdout = new List<string>();
        var stderr = new List<string>();

        var previousOut = Console.Out;
        try
        {
            if (!TryCompile(code, out var script, out var errorDiagnostics))
            {
                foreach (var diag in errorDiagnostics)
                {
                    stderr.Add(diag.ToString());
                }
                return new Result(stdout, stderr);
            }

            var writer = new StringWriter();
            Console.SetOut(writer);

            var entryPoint = _previousCompilation.GetEntryPoint(CancellationToken.None);
            var type = script.GetType(
                $"{entryPoint.ContainingNamespace.MetadataName}.{entryPoint.ContainingType.MetadataName}"
            );
            var entryPointMethod = type.GetMethod(entryPoint.MetadataName);

            var submission =
                (Func<object[], Task>)entryPointMethod.CreateDelegate(typeof(Func<object[], Task>));

            if (_submissionIndex >= _submissionStates.Length)
            {
                Array.Resize(
                    ref _submissionStates,
                    Math.Max(_submissionIndex, _submissionStates.Length * 2)
                );
            }

            var returnValue = await ((Task<object>)submission(_submissionStates));
            if (returnValue != null)
            {
                stdout.Add(CSharpObjectFormatter.Instance.FormatObject(returnValue));
            }

            var output = writer.ToString();
            if (!string.IsNullOrWhiteSpace(output))
            {
                stdout.Add(output);
            }
        }
        catch (Exception ex)
        {
            stderr.Add(ex.Message);
        }
        finally
        {
            Console.SetOut(previousOut);
        }
        return new Result(stdout, stderr);
    }

    private bool TryCompile(
        string source,
        out Assembly assembly,
        out IEnumerable<Diagnostic> errorDiagnostics
    )
    {
        assembly = null;
        var scriptCompilation = CSharpCompilation.CreateScriptCompilation(
            Path.GetRandomFileName(),
            CSharpSyntaxTree.ParseText(
                source,
                CSharpParseOptions.Default
                    .WithKind(SourceCodeKind.Script)
                    .WithLanguageVersion(LanguageVersion.Preview)
            ),
            _references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, usings: _usings),
            _previousCompilation
        );

        errorDiagnostics = scriptCompilation
            .GetDiagnostics()
            .Where(x => x.Severity == DiagnosticSeverity.Error);
        if (errorDiagnostics.Any())
        {
            return false;
        }

        using (var peStream = new MemoryStream())
        {
            var emitResult = scriptCompilation.Emit(peStream);

            if (emitResult.Success)
            {
                _submissionIndex++;
                _previousCompilation = scriptCompilation;
                assembly = Assembly.Load(peStream.ToArray());
                return true;
            }
        }

        return false;
    }
}
