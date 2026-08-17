using ShellLang;
using ShellLangTest;
using Xunit;

public sealed class BootstrapExampleTests
{
	[Fact]
	public void FullMapBootstrap()
	{
		var result = BootstrapRunner.Execute(Path.Combine(AppContext.BaseDirectory, "EXAMPLE.md"));

		Assert.True(result.Registration.Success,
			string.Join(Environment.NewLine, result.Registration.Diagnostics));
		Assert.NotNull(result.Compilation);
		Assert.True(result.Compilation.IsValid,
			string.Join(Environment.NewLine, result.Compilation.Diagnostics));
		Assert.NotNull(result.Execution);
		Assert.Equal(ExecutionStatus.Completed, result.Execution.Status);
		Assert.Equal(280, result.ScriptLineCount);
		Assert.Equal(4, result.SpawnedMonsters);
		Assert.Equal(1, result.SpawnedPlayers);
		Assert.Equal(5, result.GrantedWeaponCount);
		Assert.Equal(6, result.GrantedItemCount);
		Assert.Contains(result.Trace, line =>
			line.StartsWith("set_loading_stage(name: \"ready\") -> Ok<Void>", StringComparison.Ordinal));
		Assert.Contains(result.Trace, line =>
			line.StartsWith("log_map_started(player <- Morgan,", StringComparison.Ordinal));
	}
}
