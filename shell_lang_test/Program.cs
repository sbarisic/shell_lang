using ShellLang;
using ShellLangTest;

public static class Program
{
	public static int Main(string[] args)
	{
		var mode = args.Length == 0 ? "--console" : args.Single();
		if (mode is not ("--console" or "--example"))
		{
			Console.Error.WriteLine("Usage: shell_lang_test [--console|--example]");
			return 2;
		}

		try
		{
			if (mode == "--console")
				InteractiveConsole.Run();
			else
				ExampleConsole.Run();
			return 0;
		}
		catch (Exception ex)
		{
			Console.Error.WriteLine("FAILED: " + ex.Message);
			Console.Error.WriteLine(ex.StackTrace);
			return 1;
		}
	}
}

internal static class InteractiveConsole
{
	public static void Run()
	{
		var engine = new ShellEngine();
		var game = new MockGame(Console.WriteLine);
		var registration = game.Register(engine);
		if (!registration.Success)
			throw new InvalidOperationException("Console host registration failed: " + string.Join(Environment.NewLine, registration.Diagnostics));

		var session = new ShellSession();
		Console.WriteLine("ShellLang in-game console");
		Console.WriteLine("Enter an expression and press Enter. Type 'help' for commands or 'exit' to close.");

		while (true)
		{
			Console.Write("> ");
			var source = Console.ReadLine();
			if (source is null || source.Trim() is "exit" or "quit")
				break;
			if (string.IsNullOrWhiteSpace(source))
				continue;
			if (TryShowHelp(engine, source.Trim()))
				continue;

			var compilation = engine.Compile(source, session);
			if (!compilation.IsValid)
			{
				foreach (var diagnostic in compilation.Diagnostics)
					Console.WriteLine($"{diagnostic.Code} ({diagnostic.Source.Line},{diagnostic.Source.Column}): {diagnostic.Message}");
				continue;
			}

			var result = engine.Execute(compilation, session);
			switch (result.Status)
			{
				case ExecutionStatus.Completed:
					if (result.Value is not null && result.Value.Value is not ShellResultValue.VoidSuccess)
						Console.WriteLine(engine.FormatValue(result.Value, session));
					break;
				case ExecutionStatus.RuntimeFault:
					Console.WriteLine($"{result.RuntimeFault!.Code.Value}: {result.RuntimeFault.Message}");
					break;
				case ExecutionStatus.HostFault:
					Console.WriteLine($"{result.HostFault!.Code}: {result.HostFault.Message}");
					break;
			}
		}
	}

	private static bool TryShowHelp(ShellEngine engine, string source)
	{
		if (source == "help")
		{
			Console.WriteLine("Console commands:");
			Console.WriteLine("  help [name]  List commands or show detailed help.");
			Console.WriteLine("  exit         Close the console (quit also works).");
			Console.WriteLine();
			Console.WriteLine("ShellLang commands:");
			foreach (var command in engine.Catalog.Commands)
				Console.WriteLine($"  {command.Name,-28} {command.Description}");
			Console.WriteLine();
			Console.WriteLine("Collection and Result intrinsics:");
			foreach (var intrinsic in engine.Catalog.Intrinsics)
				Console.WriteLine($"  {intrinsic.Name,-28} {intrinsic.Description}");
			Console.WriteLine();
			Console.WriteLine("Use 'help <name>' for details.");
			return true;
		}

		const string prefix = "help ";
		if (!source.StartsWith(prefix, StringComparison.Ordinal))
			return false;

		var name = source[prefix.Length..].Trim();
		if (name.Length == 0)
		{
			Console.WriteLine("Usage: help [command]");
			return true;
		}

		var help = FindHelp(engine, name);
		if (help is null)
		{
			Console.WriteLine($"No command or symbol named '{name}'. Type 'help' to list commands.");
			return true;
		}

		PrintHelp(engine, help);
		return true;
	}

	private static HelpItem? FindHelp(ShellEngine engine, string name)
	{
		foreach (var command in engine.Catalog.Commands)
			if (command.Name == name)
				return engine.GetHelp(command.Id);
		foreach (var intrinsic in engine.Catalog.Intrinsics)
			if (intrinsic.Name == name)
				return engine.GetHelp(intrinsic.Id);
		foreach (var global in engine.Catalog.Globals)
			if (global.Name == name)
				return engine.GetHelp(global.Id);
		foreach (var type in engine.Catalog.Types)
			if (type.Name == name)
				return engine.GetHelp(type.SymbolId);
		foreach (var type in engine.Catalog.Enums)
			if (type.Name == name)
				return engine.GetHelp(type.SymbolId);
		foreach (var type in engine.Catalog.Errors)
			if (type.Name == name)
				return engine.GetHelp(type.SymbolId);
		return null;
	}

	private static void PrintHelp(ShellEngine engine, HelpItem help)
	{
		Console.WriteLine($"{help.Name} ({help.Kind})");
		Console.WriteLine(help.Description);
		PrintParameters("Inputs", help.Inputs);
		PrintParameters("Arguments", help.Arguments);
		PrintParameters("Outputs", help.Outputs);
		if (help.ContextType is { } contextType)
			Console.WriteLine($"Context: {engine.Catalog.GetTypeName(contextType)}");
		if (help.ErrorType is { } errorType)
			Console.WriteLine($"Error: {engine.Catalog.GetTypeName(errorType)}");
		if (help.RuntimeFaults.Count > 0)
			Console.WriteLine($"Runtime faults: {string.Join(", ", help.RuntimeFaults.Select(fault => fault.Code.Value))}");
		if (help.Members.Count > 0)
			Console.WriteLine($"Members: {string.Join(", ", help.Members)}");

		void PrintParameters(string heading, IReadOnlyList<HelpParameter> parameters)
		{
			if (parameters.Count == 0)
				return;
			Console.WriteLine(heading + ":");
			foreach (var parameter in parameters)
			{
				var flags = new List<string>();
				if (parameter.IsDefault)
					flags.Add("default");
				if (!parameter.Required)
					flags.Add("optional");
				var suffix = flags.Count == 0 ? string.Empty : $" ({string.Join(", ", flags)})";
				Console.WriteLine($"  {parameter.Name}: {engine.Catalog.GetTypeName(parameter.Type)}{suffix} - {parameter.Description}");
			}
		}
	}

}

internal static class ExampleConsole
{
	public static void Run()
	{
		var path = Path.Combine(AppContext.BaseDirectory, "EXAMPLE.md");
		var result = BootstrapRunner.Execute(path);
		if (!result.Registration.Success)
			throw new InvalidOperationException("Example registration failed: " + string.Join(Environment.NewLine, result.Registration.Diagnostics));
		if (!result.Compilation!.IsValid)
			throw new InvalidOperationException("Example did not compile:" + Environment.NewLine +
				string.Join(Environment.NewLine, result.Compilation.Diagnostics.Take(30)));
		if (result.Execution!.Status != ExecutionStatus.Completed)
			throw new InvalidOperationException($"Example execution failed: {result.Execution.RuntimeFault?.Code.Value} {result.Execution.RuntimeFault?.Message}{result.Execution.HostFault?.Code} {result.Execution.HostFault?.Message}");
		Console.WriteLine($"Example completed: {result.SpawnedMonsters} monsters, {result.SpawnedPlayers} player, {result.GrantedWeaponCount} weapons, {result.GrantedItemCount} starter items.");
		foreach (var item in result.Trace)
			Console.WriteLine("  " + item);
	}
}
