using ShellLang;
using ShellLangTest;
using Xunit;

public sealed class DocumentationTests
{
	[Fact]
	public void MarkedShellLangFencesCompile()
	{
		foreach (var document in new[] { "README.md", "LANGUAGE.md", "EXAMPLE.md" })
		{
			var fences = MarkdownCodeFences.ReadShellLang(Path.Combine(AppContext.BaseDirectory, document));
			Assert.True(fences.Count > 0, $"{document} must contain at least one ```shelllang fence.");

			foreach (var fence in fences)
			{
				var engine = new ShellEngine();
				var game = new MockGame();
				var registration = game.Register(engine);
				Assert.True(registration.Success,
					$"{fence.DocumentName}:{fence.SourceLine}: host registration failed.{Environment.NewLine}" +
					string.Join(Environment.NewLine, registration.Diagnostics));
				var compilation = engine.Compile(fence.Source, new ShellSession());
				Assert.True(compilation.IsValid,
					$"{fence.DocumentName}:{fence.SourceLine}: ShellLang fence did not compile.{Environment.NewLine}" +
					string.Join(Environment.NewLine, compilation.Diagnostics.Select(diagnostic =>
						$"{diagnostic.Code} ({diagnostic.Source.Line},{diagnostic.Source.Column}): {diagnostic.Message}")));
				Assert.Empty(compilation.Diagnostics);
			}
		}
	}

	[Fact]
	public void EventDesignCoversTheDeferredRuntimeContract()
	{
		var source = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "EVENTS.md"));
		foreach (var required in new[]
		{
			"EventDescriptor", "EventSink", "EventExecutionOptions", "EventExecutionHandle", "ShellEngine.Subscribe",
			"exactly one expression pipeline", "exclusively leases one `ShellSession`", "thread-safe FIFO queue",
			"TaskScheduler.Default", "default is 64", "overflow terminates", "IAsyncDisposable",
			"typed `Err` is a normal completed delivery result", "catalog-based", "`Stream<T>`"
		})
			Assert.Contains(required, source, StringComparison.Ordinal);
		Assert.DoesNotContain("```shelllang", source, StringComparison.Ordinal);
		Assert.Contains("EVENTS.md", File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "README.md")),
			StringComparison.Ordinal);
		Assert.Contains("EVENTS.md", File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "LANGUAGE.md")),
			StringComparison.Ordinal);
	}
}
