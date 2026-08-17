using ShellLang;
using ShellLangTest;

internal sealed record HierarchyProbe(string Value);
internal sealed record MutationReceiver(int Value);

internal sealed class CallbackObserver(Action callback) : IExecutionObserver
{
	public void StatementCompleted(int statementIndex, SourceSpan source, ShellValue? value) => callback();
}

internal static partial class Conformance
{
	private static void NominalHierarchy()
	{
		var engine = new ShellEngine();
		var core = engine.Core;
		TypeDescriptorBuilder<HierarchyProbe> Type(string name) => TypeDescriptorBuilder.For<HierarchyProbe>(name).Description($"{name} type.");
		var root = Type("HierarchyRoot")
			.Member("root", "Root member.", core.String, x => x.Value)
			.Member("shared", "Shared member.", core.String, _ => "shared").Build();
		var left = Type("HierarchyLeft").Base(root.Id)
			.Member("left", "Left member.", core.String, _ => "left")
			.Member("choice", "Left choice.", core.String, _ => "left").Build();
		var right = Type("HierarchyRight").Base(root.Id)
			.Member("right", "Right member.", core.String, _ => "right").Build();
		var specific = Type("HierarchySpecific").Base(left.Id)
			.Member("choice", "Specific choice.", core.String, _ => "specific").Build();
		var joinA = Type("HierarchyJoinA").Base(left.Id).Base(specific.Id).Base(right.Id).Build();
		var joinB = Type("HierarchyJoinB").Base(right.Id).Base(specific.Id).Base(left.Id).Build();
		var conflictLeft = Type("ConflictLeft").Base(root.Id)
			.Member("conflict", "Left conflict.", core.String, _ => "left").Build();
		var conflictRight = Type("ConflictRight").Base(root.Id)
			.Query("conflict", "Right conflict.", null, core.String, (_, _, _) => "right").Build();
		var resolved = Type("ConflictResolved").Base(conflictLeft.Id).Base(conflictRight.Id)
			.Member("conflict", "Explicit conflict resolution.", core.String, _ => "resolved").Build();
		var registration = engine.Register(new DescriptorSet(types:
			[joinB, right, root, specific, left, joinA, conflictRight, conflictLeft, resolved]));
		True(registration.Success, string.Join(Environment.NewLine, registration.Diagnostics));

		True(engine.Catalog.IsAssignable(joinA.Id, root.Id));
		True(!engine.Catalog.IsAssignable(root.Id, joinA.Id));
		True(engine.Catalog.IsAssignable(engine.Catalog.ArrayOf(joinA.Id), engine.Catalog.ArrayOf(root.Id)));

		var game = new MockGame();
		var gameEngine = new ShellEngine();
		True(game.Register(gameEngine).Success);
		var gameSession = new ShellSession();
		var common = gameEngine.Compile("find_entities(classname: \"info_spawn\") -> validate_player_spawns(navigation <- navigation) -> choose_random_spawn(seed: 1)", gameSession);
		Valid(common);
		Equal("Result<MapMarker,MapError>", gameEngine.Catalog.GetTypeName(common.ResultType!.Value));
		var gameErrors = gameEngine.Catalog.Errors.ToDictionary(x => x.Name, x => x.Id, StringComparer.Ordinal);
		True(gameEngine.Catalog.IsAssignable(gameErrors["NavigationError"], gameErrors["MapError"]));
		True(gameEngine.Catalog.IsAssignable(gameErrors["SpawnError"], gameErrors["MapError"]));
		True(gameEngine.Catalog.IsAssignable(gameErrors["InventoryError"], gameErrors["PlayerError"]));
		True(!gameEngine.Catalog.IsAssignable(gameErrors["PlayerError"], gameErrors["InventoryError"]));

		var session = new ShellSession();
		session.SetBinding("probe", engine.CreateValue(joinA.Id, new HierarchyProbe("root-value")));
		Equal("specific", Run(engine, session, "probe.choice").Value!.Get<string>());
		var helpA = engine.GetHelp(joinA.SymbolId)!;
		var helpB = engine.GetHelp(joinB.SymbolId)!;
		Equal(string.Join("|", helpA.Members), string.Join("|", helpB.Members));
		Equal(1, helpA.Members.Count(x => x == "shared"));
		var completions = engine.GetCompletions("probe.", 6, session).Items;
		Equal(1, completions.Count(x => x.InsertionText == "shared"));
		var formatted = engine.FormatValue(engine.CreateValue(joinA.Id, new HierarchyProbe("root-value")), session);
		Equal(1, formatted.Split("shared:", StringSplitOptions.None).Length - 1);
		session.SetBinding("resolved", engine.CreateValue(resolved.Id, new HierarchyProbe("value")));
		Equal("resolved", Run(engine, session, "resolved.conflict").Value!.Get<string>());

		var ambiguousEngine = new ShellEngine();
		var ambiguousCore = ambiguousEngine.Core;
		var ambiguousRoot = TypeDescriptorBuilder.For<HierarchyProbe>("AmbiguousRoot").Build();
		var ambiguousLeft = TypeDescriptorBuilder.For<HierarchyProbe>("AmbiguousLeft").Base(ambiguousRoot.Id)
			.Member("collision", "Collision member.", ambiguousCore.String, _ => "left").Build();
		var ambiguousRight = TypeDescriptorBuilder.For<HierarchyProbe>("AmbiguousRight").Base(ambiguousRoot.Id)
			.Query("collision", "Collision query.", null, ambiguousCore.String, (_, _, _) => "right").Build();
		var ambiguousJoin = TypeDescriptorBuilder.For<HierarchyProbe>("AmbiguousJoin")
			.Base(ambiguousRight.Id).Base(ambiguousLeft.Id).Build();
		var revision = ambiguousEngine.CatalogRevision;
		var typeCount = ambiguousEngine.Catalog.Types.Count;
		var rejected = ambiguousEngine.Register(new DescriptorSet(types: [ambiguousJoin, ambiguousRight, ambiguousRoot, ambiguousLeft]));
		True(!rejected.Success);
		True(rejected.Diagnostics.Any(x => x.Code == "SL3021"));
		Equal(revision, ambiguousEngine.CatalogRevision);
		Equal(typeCount, ambiguousEngine.Catalog.Types.Count);
	}

	private static void SessionBindingIsolation()
	{
		var engine = new ShellEngine();
		var core = engine.Core;
		var session = new ShellSession();
		Action mutation = static () => { };
		var receiver = TypeDescriptorBuilder.For<MutationReceiver>("MutationReceiver")
			.Description("Session mutation receiver.")
			.Member("mutate_member", "Attempt mutation from a member.", core.Int32, value =>
			{
				mutation();
				return value.Value;
			})
			.Query("mutate_query", "Attempt mutation from a query.", null, core.Int32, (_, value, _) =>
			{
				mutation();
				return value.Value;
			}).Build();
		var global = new GlobalDescriptor("mutation_global", "Attempt mutation from a global.", core.Int32, context =>
		{
			mutation();
			return context.Engine.CreateValue(core.Int32, 1);
		});
		var command = new CommandDescriptor("mutate_binding", "Attempt mutation from a command.", null, null, null, (_, _) =>
		{
			mutation();
			return CommandOutcome.Success.Empty;
		});
		var hostState = 0;
		var stateCommand = new CommandDescriptor("mutate_host_state", "Mutate declared host state.", null, null, null, (_, _) =>
		{
			hostState++;
			return CommandOutcome.Success.Empty;
		});
		ExecutionResult? nestedResult = null;
		ShellCompilation? nestedCompilation = null;
		var reenter = new CommandDescriptor("reenter_session", "Attempt recursive execution.", null, null, null, (context, _) =>
		{
			nestedResult = context.Engine.Execute(nestedCompilation!, context.Session);
			return CommandOutcome.Success.Empty;
		});
		var registration = engine.Register(new DescriptorSet(types: [receiver], globals: [global],
			commands: [command, stateCommand, reenter]));
		True(registration.Success, string.Join(Environment.NewLine, registration.Diagnostics));

		session.SetBinding("protected_value", engine.CreateValue(core.Int32, 1));
		session.SetBinding("mutation_receiver", engine.CreateValue(receiver.Id, new MutationReceiver(7)));
		var readCompilation = engine.Compile("protected_value + 1", session);
		Valid(readCompilation);
		nestedCompilation = engine.Compile("1", session);
		Valid(nestedCompilation);

		var boundaries = new (string Name, Func<ExecutionResult> Execute)[]
		{
			("command", () => Execute("mutate_binding()")),
			("query", () => Execute("mutation_receiver.mutate_query()")),
			("member", () => Execute("mutation_receiver.mutate_member")),
			("global", () => Execute("mutation_global")),
			("observer", () => Execute("1", new ExecutionOptions { Observer = new CallbackObserver(() => mutation()) }))
		};
		var attempts = new (string Name, Action Mutate)[]
		{
			("same type", () => session.SetBinding("protected_value", engine.CreateValue(core.Int32, 2))),
			("different type", () => session.SetBinding("protected_value", engine.CreateValue(core.String, "changed"))),
			("removal", () => session.RemoveBinding("protected_value"))
		};
		foreach (var boundary in boundaries)
			foreach (var attempt in attempts)
			{
				mutation = attempt.Mutate;
				var revision = session.SchemaRevision;
				var result = boundary.Execute();
				Equal(ExecutionStatus.HostFault, result.Status);
				True(result.HostFault!.Exception is InvalidOperationException,
					$"{boundary.Name}/{attempt.Name} did not preserve the rejected mutation exception.");
				True(session.TryGetBinding("protected_value", out var retained));
				Equal(core.Int32, retained.Type);
				Equal(1, retained.Get<int>());
				Equal(revision, session.SchemaRevision);
				var read = engine.Execute(readCompilation, session);
				Equal(ExecutionStatus.Completed, read.Status);
				Equal(2, read.Value!.Get<int>());
			}

		mutation = static () => { };
		Equal(ExecutionStatus.Completed, Execute("created = 1").Status);
		var addedRevision = session.SchemaRevision;
		Equal(ExecutionStatus.Completed, Execute("created = 2").Status);
		Equal(addedRevision, session.SchemaRevision);
		Equal(ExecutionStatus.Completed, Execute("created = \"changed\"").Status);
		Equal(addedRevision + 1, session.SchemaRevision);
		Equal(ExecutionStatus.Completed, Execute("mutate_host_state()").Status);
		Equal(1, hostState);
		Equal(ExecutionStatus.Completed, Execute("reenter_session()").Status);
		Equal(ExecutionStatus.HostFault, nestedResult!.Status);
		Equal("SL5005", nestedResult.HostFault!.Code);

		ExecutionResult Execute(string source, ExecutionOptions? options = null)
		{
			var compilation = engine.Compile(source, session);
			Valid(compilation);
			return engine.Execute(compilation, session, options);
		}
	}

	private static void CommandBuilderDeclarations()
	{
		var engine = new ShellEngine();
		var core = engine.Core;
		var command = CommandDescriptorBuilder.Create("builder_add")
			.Description("Add an argument to the input.")
			.Input("value", core.Int32, isDefault: true, description: "Base value.")
			.Argument("amount", core.Int32, description: "Amount to add.")
			.Output("result", core.Int32, isDefault: true, description: "Sum.")
			.Invoke((context, values) => CommandOutcome.Success.Single("result", context.Engine.CreateValue(core.Int32,
				values.GetInput<int>("value") + values.GetArgument<int>("amount"))))
			.Build();
		var registration = engine.Register(new DescriptorSet(commands: [command]));
		True(registration.Success, string.Join(Environment.NewLine, registration.Diagnostics));
		Equal(core.Int32, command.Inputs.Single().Type);
		Equal(core.Int32, command.Arguments.Single().Type);
		Equal(core.Int32, command.Outputs.Single().Type);
		var result = Run(engine, new ShellSession(), "2 -> builder_add(3)");
		Equal(5, result.Value!.Get<int>());
	}
}
