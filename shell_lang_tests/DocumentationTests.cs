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
			}
		}
	}
}
