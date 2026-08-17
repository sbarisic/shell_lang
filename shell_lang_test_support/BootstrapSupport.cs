using System.Collections.Immutable;
using ShellLang;

namespace ShellLangTest;

internal sealed record MarkdownCodeFence(string DocumentName, int SourceLine, string Source);

internal static class MarkdownCodeFences
{
	private const string ShellLangFence = "```shelllang";

	public static IReadOnlyList<MarkdownCodeFence> ReadShellLang(string path)
	{
		var text = File.ReadAllText(path).Replace("\r\n", "\n", StringComparison.Ordinal);
		var lines = text.Split('\n');
		var fences = new List<MarkdownCodeFence>();
		for (var index = 0; index < lines.Length; index++)
		{
			if (lines[index] != ShellLangFence)
				continue;
			var openingLine = index + 1;
			var sourceStart = ++index;
			while (index < lines.Length && lines[index] != "```")
				index++;
			if (index == lines.Length)
				throw new InvalidOperationException($"{Path.GetFileName(path)}:{openingLine} has an unterminated ShellLang fence.");
			fences.Add(new MarkdownCodeFence(Path.GetFileName(path), sourceStart + 1,
				string.Join("\n", lines[sourceStart..index])));
		}
		return fences;
	}
}

internal sealed record BootstrapRunResult(
	RegistrationResult Registration,
	ShellCompilation? Compilation,
	ExecutionResult? Execution,
	int ScriptLineCount,
	int SpawnedMonsters,
	int SpawnedPlayers,
	int GrantedWeaponCount,
	int GrantedItemCount,
	ImmutableArray<string> Trace);

internal static class BootstrapRunner
{
	public static BootstrapRunResult Execute(string examplePath)
	{
		var fence = MarkdownCodeFences.ReadShellLang(examplePath).Single();
		var engine = new ShellEngine();
		var game = new MockGame();
		var registration = game.Register(engine);
		if (!registration.Success)
			return Result(registration, null, null);
		var session = new ShellSession();
		var compilation = engine.Compile(fence.Source, session);
		var execution = compilation.IsValid ? engine.Execute(compilation, session) : null;
		return Result(registration, compilation, execution);

		BootstrapRunResult Result(RegistrationResult registered, ShellCompilation? compiled, ExecutionResult? executed) =>
			new(registered, compiled, executed, fence.Source.Split('\n').Length, game.SpawnedMonsters, game.SpawnedPlayers,
				game.GrantedWeapons.Count, game.GrantedItems.Count, game.Trace.ToImmutableArray());
	}
}
