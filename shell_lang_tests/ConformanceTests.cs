using ShellLang;
using ShellLangTest;
using Xunit;

internal sealed record IntrinsicProbe(int Value);

public sealed partial class ConformanceTests
{
	[Fact]
	public void LiteralsAndOperators()
	{
		var (engine, _, session) = Fixture();
		var compilation = engine.Compile("x = 10\ny = 2\n(x + y * 5) == 20", session);
		Valid(compilation);
		var result = engine.Execute(compilation, session);
		Assert.Equal(ExecutionStatus.Completed, result.Status);
		Assert.True(result.Value!.Get<bool>());
		Assert.True(session.TryGetBinding("x", out var x));
		Assert.Equal(10, x.Get<int>());
		result = engine.Execute(engine.Compile("local_player.name_length()", session), session);
		Assert.Equal(6, result.Value!.Get<int>());
	}

	[Fact]
	public void Continuation()
	{
		var (engine, _, session) = Fixture();
		var multiline = engine.Compile("map\n  .name\n  -> print", session);
		Valid(multiline);
		Assert.Equal(ExecutionStatus.Completed, engine.Execute(multiline, session).Status);
		var hard = engine.Compile("map;\n -> print", session);
		Assert.True(!hard.IsValid, "A semicolon must prevent pipeline continuation.");
	}

	[Fact]
	public void ArraysAndReducers()
	{
		var (engine, _, session) = Fixture();
		var result = Run(engine, session, "[1, 2, 3, 4] -> sum");
		Assert.Equal(10, result.Value!.Get<int>());
		result = Run(engine, session, "[3, 1, 2] -> min -> require");
		Assert.Equal(1, result.Value!.Get<int>());
		result = Run(engine, session, "find_entities(classname: \"info_spawn\") -> where(.spawn_order > 1) -> count");
		Assert.Equal(2, result.Value!.Get<int>());
	}

	[Fact]
	public void ExpandedCollectionIntrinsics()
	{
		var (engine, _, session) = Fixture();
		var core = engine.Core;

		var result = Run(engine, session, "[10, 20, 30] -> at(-1)");
		Assert.Equal(30, result.Value!.Get<int>());
		result = Run(engine, session, "[10, 20, 30] -> at(-3)");
		Assert.Equal(10, result.Value!.Get<int>());
		result = Run(engine, session, "[10, 20, 30] -> at(2)");
		Assert.Equal(30, result.Value!.Get<int>());
		result = Run(engine, session, "[10, 20] -> at([1] -> first) -> require");
		Assert.Equal(20, result.Value!.Get<int>());
		result = engine.Execute(engine.Compile("[10, 20, 30] -> at(3)", session), session);
		Assert.Equal(ExecutionStatus.RuntimeFault, result.Status);
		Assert.Equal("SL4006", result.RuntimeFault!.Code.Value);
		result = engine.Execute(engine.Compile("[1] -> skip(1) -> at(0)", session), session);
		Assert.Equal(ExecutionStatus.RuntimeFault, result.Status);
		Assert.Equal("SL4006", result.RuntimeFault!.Code.Value);

		result = Run(engine, session, "[1, 2, 3] -> skip(count: 1) -> sum");
		Assert.Equal(5, result.Value!.Get<int>());
		result = Run(engine, session, "[1, 2, 3] -> skip(99) -> count");
		Assert.Equal(0, result.Value!.Get<int>());
		result = Run(engine, session, "[1, 2, 3] -> slice(start: -2, count: 2) -> sum");
		Assert.Equal(5, result.Value!.Get<int>());
		result = Run(engine, session, "[1, 2, 3] -> slice(3, 0) -> count");
		Assert.Equal(0, result.Value!.Get<int>());
		result = engine.Execute(engine.Compile("[1, 2, 3] -> slice(2, 2)", session), session);
		Assert.Equal(ExecutionStatus.RuntimeFault, result.Status);
		Assert.Equal("SL4007", result.RuntimeFault!.Code.Value);
		result = engine.Execute(engine.Compile("[1, 2, 3] -> slice(-4, 1)", session), session);
		Assert.Equal(ExecutionStatus.RuntimeFault, result.Status);
		Assert.Equal("SL4007", result.RuntimeFault!.Code.Value);
		result = engine.Execute(engine.Compile("[1, 2, 3] -> slice(4, 0)", session), session);
		Assert.Equal(ExecutionStatus.RuntimeFault, result.Status);
		Assert.Equal("SL4007", result.RuntimeFault!.Code.Value);
		result = engine.Execute(engine.Compile("n = -1\n[1, 2, 3] -> slice(0, n)", session), session);
		Assert.Equal(ExecutionStatus.RuntimeFault, result.Status);
		Assert.Equal("SL4004", result.RuntimeFault!.Code.Value);
		Assert.True(!engine.Compile("[1] -> skip(-1)", session).IsValid);
		Run(engine, session, "original = [1, 2, 3]");
		Run(engine, session, "original -> slice(1, 1)");
		result = Run(engine, session, "original -> count");
		Assert.Equal(3, result.Value!.Get<int>());

		result = Run(engine, session, "find_entities(classname: \"info_spawn\") -> any(.spawn_order == 2)");
		Assert.True(result.Value!.Get<bool>());
		result = Run(engine, session, "find_entities(classname: \"info_spawn\") -> all(predicate: .spawn_order > 0)");
		Assert.True(result.Value!.Get<bool>());
		result = Run(engine, session, "find_entities(classname: \"missing\") -> any(.spawn_order > 0)");
		Assert.False(result.Value!.Get<bool>());
		result = Run(engine, session, "find_entities(classname: \"missing\") -> all(.spawn_order > 0)");
		Assert.True(result.Value!.Get<bool>());
		result = Run(engine, session, "find_entities(classname: \"info_spawn\") -> select(selector: .spawn_order * 2) -> sum");
		Assert.Equal(12, result.Value!.Get<int>());
		result = Run(engine, session, "find_entities(classname: \"missing\") -> select(.spawn_order) -> count");
		Assert.Equal(0, result.Value!.Get<int>());

		result = Run(engine, session, "[1, 2, 3] -> contains(value: 2)");
		Assert.True(result.Value!.Get<bool>());
		result = Run(engine, session, "[1, 2, 3] -> contains(9)");
		Assert.False(result.Value!.Get<bool>());
		result = Run(engine, session, "[1, 2, 1, 3, 2] -> distinct -> count");
		Assert.Equal(3, result.Value!.Get<int>());
		result = Run(engine, session, "find_entities(classname: \"info_spawn\") -> concat(find_entities(classname: \"info_spawn\")) -> distinct(by: .stable_id) -> count");
		Assert.Equal(3, result.Value!.Get<int>());
		result = Run(engine, session, "[1, 2] -> concat(other: [3, 4]) -> reverse -> at(0)");
		Assert.Equal(4, result.Value!.Get<int>());
		result = Run(engine, session, "[1, 2, 3] -> last -> require");
		Assert.Equal(3, result.Value!.Get<int>());
		result = Run(engine, session, "[1] -> skip(1) -> last -> is_ok");
		Assert.False(result.Value!.Get<bool>());
		result = Run(engine, session, "[42] -> single -> require");
		Assert.Equal(42, result.Value!.Get<int>());
		result = Run(engine, session, "[1, 2] -> single -> error");
		var cardinality = result.Value!.Get<CollectionCardinalityError>();
		Assert.Equal(2, cardinality.ActualCount);
		result = Run(engine, session, "[1] -> skip(1) -> single -> error");
		Assert.Equal(0, result.Value!.Get<CollectionCardinalityError>().ActualCount);

		var calls = 0;
		var probe = TypeDescriptorBuilder.For<IntrinsicProbe>("IntrinsicProbe")
			.Description("Collection intrinsic test probe.")
			.Member("value", "Probe value.", core.Int32, x => x.Value)
			.FallibleQuery("test", "Test the probe.", null, core.Bool, core.Error, (context, value, _) =>
			{
				calls++;
				return value.Value == 2
					? new QueryOutcome.Error(context.Engine.CreateValue(core.Error, new ShellError("probe error")))
					: new QueryOutcome.Success(context.Engine.CreateValue(core.Bool, value.Value > 0));
			})
			.Equality((left, right) => left.Value == 99 || right.Value == 99
				? throw new InvalidOperationException("comparison failed")
				: left.Value == right.Value)
			.Build();
		var baseActor = TypeDescriptorBuilder.For<BaseActor>("IntrinsicBaseActor").Build();
		var derivedActor = TypeDescriptorBuilder.For<DerivedActor>("IntrinsicDerivedActor").Base(baseActor.Id).Build();
		Assert.True(engine.Register(new DescriptorSet(types: [probe, baseActor, derivedActor])).Success);

		ShellValue Probe(int value) => engine.CreateValue(probe.Id, new IntrinsicProbe(value));
		session.SetBinding("probes", engine.CreateArray(probe.Id, [Probe(1), Probe(2), Probe(0)]));
		calls = 0;
		result = Run(engine, session, "probes -> any(.test()) -> require");
		Assert.True(result.Value!.Get<bool>());
		Assert.Equal(1, calls);
		session.SetBinding("probes", engine.CreateArray(probe.Id, [Probe(0), Probe(2), Probe(1)]));
		calls = 0;
		result = Run(engine, session, "probes -> all(.test()) -> require");
		Assert.False(result.Value!.Get<bool>());
		Assert.Equal(1, calls);
		result = Run(engine, session, "probes -> any(.test())");
		var propagated = (ShellResultValue.Error)result.Value!.Value;
		Assert.Equal(1, propagated.Frames.Single(x => x.ArrayIndex is not null).ArrayIndex!.Value);
		calls = 0;
		result = Run(engine, session, "probes -> select(.test())");
		propagated = (ShellResultValue.Error)result.Value!.Value;
		Assert.Equal(1, propagated.Frames.Single(x => x.ArrayIndex is not null).ArrayIndex!.Value);
		Assert.Equal(2, calls);
		calls = 0;
		result = Run(engine, session, "probes -> distinct(by: .test())");
		propagated = (ShellResultValue.Error)result.Value!.Value;
		Assert.Equal(1, propagated.Frames.Single(x => x.ArrayIndex is not null).ArrayIndex!.Value);
		Assert.Equal(2, calls);
		session.SetBinding("probes", engine.CreateArray(probe.Id, [Probe(0), Probe(1), Probe(1)]));
		result = Run(engine, session, "probes -> select(.test()) -> count -> require");
		Assert.Equal(3, result.Value!.Get<int>());
		result = Run(engine, session, "probes -> distinct(by: .test()) -> count -> require");
		Assert.Equal(2, result.Value!.Get<int>());

		session.SetBinding("needle", Probe(99));
		session.SetBinding("probes", engine.CreateArray(probe.Id, [Probe(1), Probe(99)]));
		var comparison = engine.Execute(engine.Compile("probes -> contains(needle)", session), session);
		Assert.Equal(ExecutionStatus.HostFault, comparison.Status);
		Assert.Equal("SL5102", comparison.HostFault!.Code);
		comparison = engine.Execute(engine.Compile("probes -> distinct", session), session);
		Assert.Equal(ExecutionStatus.HostFault, comparison.Status);
		Assert.Equal("SL5102", comparison.HostFault!.Code);

		var derivedValue = engine.CreateValue(derivedActor.Id, new DerivedActor("derived"));
		session.SetBinding("wide", engine.CreateArray(baseActor.Id, [derivedValue]));
		session.SetBinding("narrow", engine.CreateArray(derivedActor.Id, [derivedValue]));
		result = Run(engine, session, "wide -> concat(narrow) -> count");
		Assert.Equal(2, result.Value!.Get<int>());
		Assert.True(!engine.Compile("narrow -> concat(wide)", session).IsValid);

		Assert.True(!engine.Compile("[1] -> at(foo: 0)", session).IsValid);
		Assert.True(!engine.Compile("[1] -> at()", session).IsValid);
		Assert.True(!engine.Compile("[1] -> at(\"zero\")", session).IsValid);
		Assert.True(!engine.Compile("[1] -> slice(start: 0, start: 0)", session).IsValid);
		Assert.True(!engine.Compile("[1] -> slice(start: 0, 1)", session).IsValid);
		Assert.True(!engine.Compile("find_entities(classname: \"info_spawn\") -> distinct", session).IsValid);
		var duplicate = new CommandDescriptor("at", "Reserved intrinsic name.", null, null, null, (_, _) => CommandOutcome.Success.Empty);
		Assert.True(!engine.Register(new DescriptorSet(commands: [duplicate])).Success);
		foreach (var name in new[] { "at", "last", "skip", "slice", "any", "all", "select", "contains", "concat", "distinct", "reverse", "single" })
		{
			var intrinsic = engine.Catalog.Intrinsics.Single(x => x.Name == name);
			Assert.True(!intrinsic.Description.StartsWith("Core ", StringComparison.Ordinal));
		}
		const string completionSource = "[1] -> ";
		Assert.Contains(engine.GetCompletions(completionSource, completionSource.Length, session).Items,
			x => x.InsertionText == "slice");
	}

	[Fact]
	public void EmptyRequire()
	{
		var (engine, _, session) = Fixture();
		var result = Run(engine, session, "find_entities(classname: \"missing\") -> first -> require");
		Assert.Equal(ExecutionStatus.RuntimeFault, result.Status);
		Assert.Equal("SL4001", result.RuntimeFault!.Code.Value);
	}

	[Fact]
	public void AtomicRegistration()
	{
		var (engine, _, _) = Fixture();
		var revision = engine.CatalogRevision;
		var duplicate = new CommandDescriptor("print", "Duplicate.", null, null, null, (_, _) => CommandOutcome.Success.Empty);
		var registration = engine.Register(new DescriptorSet(commands: [duplicate]));
		Assert.True(!registration.Success);
		Assert.Equal(revision, engine.CatalogRevision);
	}

	[Fact]
	public void Revisions()
	{
		var (engine, _, session) = Fixture();
		Run(engine, session, "required = 1");
		var compilation = engine.Compile("required + 1", session);
		Valid(compilation);
		session.SetBinding("required", engine.CreateValue(engine.Core.Int32, 9));
		Assert.Equal(ExecutionStatus.Completed, engine.Execute(compilation, session).Status);
		session.SetBinding("required", engine.CreateValue(engine.Core.String, "changed"));
		Assert.Equal(ExecutionStatus.HostFault, engine.Execute(compilation, session).Status);
		var fresh = engine.Compile("map.name", session);
		Valid(fresh);
		Assert.True(engine.Register(new DescriptorSet()).Success);
		Assert.Equal(ExecutionStatus.HostFault, engine.Execute(fresh, session).Status);
	}

	[Fact]
	public void Metadata()
	{
		var (engine, _, session) = Fixture();
		Assert.Contains(engine.GetCompletions("spa", 3, session).Items, x => x.InsertionText == "spawn_player");
		var command = engine.Catalog.Commands.Single(x => x.Name == "spawn_player");
		var help = engine.GetHelp(command.Id);
		Assert.Equal("spawn_player", help!.Name);
		Assert.Equal(3, help.Inputs.Count);
		Assert.Contains(engine.GetCompletions("map.na", 6, session).Items, x => x.InsertionText == "name");
	}

	[Fact]
	public void PrintOutput()
	{
		var output = new List<string>();
		var engine = new ShellEngine();
		var game = new MockGame(output.Add);
		var registration = game.Register(engine);
		Assert.True(registration.Success, string.Join(Environment.NewLine, registration.Diagnostics));
		var session = new ShellSession();

		var result = Run(engine, session, "\"Hello world\" -> print");
		Assert.Equal(ExecutionStatus.Completed, result.Status);
		Assert.True(result.Value is null, "A non-fallible print command must remain terminal Void.");
		Assert.Single(output);
		Assert.Equal("\"Hello world\"", output[0]);

		output.Clear();
		result = Run(engine, session, "[\"one\", \"two\"] -> print");
		Assert.Equal(ExecutionStatus.Completed, result.Status);
		Assert.True(result.Value is null, "Printing an array must remain terminal Void.");
		Assert.Single(output);
		Assert.Equal("[\"one\", \"two\"]", output[0]);
	}


	private static (ShellEngine Engine, MockGame Game, ShellSession Session) Fixture()
	{
		var engine = new ShellEngine();
		var game = new MockGame();
		var registration = game.Register(engine);
		if (!registration.Success)
			throw new InvalidOperationException(string.Join(Environment.NewLine, registration.Diagnostics));
		return (engine, game, new ShellSession());
	}

	private static ExecutionResult Run(ShellEngine engine, ShellSession session, string source)
	{
		var compilation = engine.Compile(source, session);
		Valid(compilation);
		var result = engine.Execute(compilation, session);
		if (result.Status == ExecutionStatus.HostFault)
			throw new InvalidOperationException($"{result.HostFault!.Code}: {result.HostFault.Message}", result.HostFault.Exception);
		return result;
	}
	private static void Valid(ShellCompilation compilation)
	{
		if (!compilation.IsValid)
			throw new InvalidOperationException(string.Join(Environment.NewLine, compilation.Diagnostics));
	}
}

public sealed class AdvancedConformanceTests
{
	[Fact]
	public void TerminalLiftingFaultContainmentAndEvaluationCounts()
	{
		var engine = new ShellEngine();
		var game = new MockGame();
		Good(game.Register(engine));
		var core = engine.Core;
		ShellTypeId Type(string name) => engine.Catalog.Types.FirstOrDefault(x => x.Name == name)?.Id
			?? engine.Catalog.Enums.FirstOrDefault(x => x.Name == name)?.Id
			?? engine.Catalog.Errors.First(x => x.Name == name).Id;
		var marker = Type("MapMarker");
		var spawnError = Type("SpawnError");
		var spawnOutput = engine.Catalog.Commands.Single(x => x.Name == "spawn_player").OutputRecordType!.Value;
		var baseActor = new TypeDescriptor("BaseActor", "Base actor.", typeof(BaseActor), new ValueAdapter<BaseActor>());
		var derivedActor = new TypeDescriptor("DerivedActor", "Derived actor.", typeof(DerivedActor), new ValueAdapter<DerivedActor>(), [baseActor.Id]);
		Good(engine.Register(new DescriptorSet(types: [baseActor, derivedActor])));
		Assert.True(engine.Catalog.IsAssignable(derivedActor.Id, baseActor.Id));
		Assert.True(engine.Catalog.IsAssignable(engine.Catalog.ArrayOf(derivedActor.Id), engine.Catalog.ArrayOf(baseActor.Id)));
		Assert.True(engine.Catalog.IsAssignable(engine.Catalog.ResultOf(derivedActor.Id, spawnError), engine.Catalog.ResultOf(baseActor.Id, engine.Core.Error)));
		Assert.True(engine.Catalog.IsAssignable(derivedActor.Id, engine.Core.Any));
		Assert.True(!engine.Catalog.IsAssignable(engine.Core.Any, derivedActor.Id));
		var touches = 0;
		var fallibleTouches = 0;
		var faultTouches = 0;
		var samples = 0;
		var boolCalls = 0;
		InputPortDescriptor Primary() => new("marker", "Marker.", marker, true);
		CommandOutcome.Success MarkerValue(InvocationValues values) => CommandOutcome.Success.Single("value", values.GetInput("marker"));
		var commands = new List<CommandDescriptor>
		{
			new("touch_marker", "Touch marker.", [Primary()], null, null, (_, _) => { touches++; return CommandOutcome.Success.Empty; }),
			new("try_marker", "Fallible terminal marker.", [Primary()], null, null, (_, _) =>
			{
				fallibleTouches++;
				return fallibleTouches == 2
					? new CommandOutcome.Error(engine.CreateValue(spawnError, new GameFailure("second marker failed")))
					: CommandOutcome.Success.Empty;
			}, spawnError),
			new("try_marker_ok", "Successful fallible terminal marker.", [Primary()], null, null, (_, _) => CommandOutcome.Success.Empty, spawnError),
			new("fault_marker", "Faulting marker value.", [Primary()], null, [new OutputPortDescriptor("value", "Marker.", marker)], (_, values) =>
			{
				faultTouches++; return faultTouches == 2 ? new CommandOutcome.Fault(new RuntimeFaultCode("GAME1001"), "deliberate marker fault") : MarkerValue(values);
			}, runtimeFaults: [new RuntimeFaultCode("GAME1001")]),
			new("explode_marker", "Throw from host.", [Primary()], null, null, (_, _) => throw new InvalidOperationException("boom")),
			new("undeclared_fault", "Return undeclared fault.", [Primary()], null, null, (_, _) => new CommandOutcome.Fault(new RuntimeFaultCode("OTHER1001"), "bad")),
			new("sample_once", "Count argument evaluation.", null, null, [new OutputPortDescriptor("value", "Count.", core.Int32)], (_, _) =>
				CommandOutcome.Success.Single("value", engine.CreateValue(core.Int32, ++samples))),
			new("touch_amount", "Touch with amount.", [Primary()], [new ArgumentDescriptor("amount", "Amount.", core.Int32, 0)], null, (_, _) => { touches++; return CommandOutcome.Success.Empty; }),
			new("load_empty_markers", "Fallible marker load.", null, null,
				[new OutputPortDescriptor("markers", "Markers.", engine.Catalog.ArrayOf(marker))], (_, _) =>
					CommandOutcome.Success.Single("markers", engine.CreateArray(marker, [])), spawnError),
			new("wrong_output", "Wrong host output.", null, null, [new OutputPortDescriptor("value", "Integer.", core.Int32)], (_, _) =>
				CommandOutcome.Success.Single("value", engine.CreateValue(core.String, "wrong"))),
			new("fail_bool", "Return a typed Boolean error.", null, null, [new OutputPortDescriptor("value", "Boolean.", core.Bool)], (_, _) =>
			{ boolCalls++; return new CommandOutcome.Error(engine.CreateValue(spawnError, new GameFailure("boolean failed"))); }, spawnError),
			new("inspect_spawn_output", "Consume a complete spawn output.", [new InputPortDescriptor("output", "Spawn output.", spawnOutput, true)], null,
				[new OutputPortDescriptor("value", "Accepted.", core.Bool)], (_, _) => CommandOutcome.Success.Single("value", engine.CreateValue(core.Bool, true))),
			new("accept_spawned_player", "Consume the default player output.", [new InputPortDescriptor("player", "Player.", Type("Player"), true)], null,
				[new OutputPortDescriptor("value", "Accepted.", core.Bool)], (_, _) => CommandOutcome.Success.Single("value", engine.CreateValue(core.Bool, true)))
		};
		Good(engine.Register(new DescriptorSet(commands: commands)));
		var session = new ShellSession();

		var result = Execute("find_entities(classname: \"info_spawn\") -> touch_marker", session);
		Completed(result);
		Assert.Equal(3, touches);
		result = Execute("find_entities(classname: \"missing\") -> touch_amount(amount: sample_once())", session);
		Completed(result);
		Assert.Equal(1, samples);
		Assert.Equal(3, touches);
		result = Execute("nested = [find_entities(classname: \"info_spawn\"), find_entities(classname: \"info_spawn\")]\nnested -> touch_marker", session);
		Completed(result);
		Assert.Equal(9, touches);
		result = Execute("ok = find_entities(classname: \"info_spawn\") -> try_marker_ok", session);
		Completed(result);
		Assert.True(session.TryGetBinding("ok", out var ok));
		Assert.True(ok.Value is ShellResultValue.VoidSuccess);
		result = Execute("failed = find_entities(classname: \"info_spawn\") -> try_marker", session);
		Completed(result);
		Assert.True(session.TryGetBinding("failed", out var failed));
		var error = failed.Value as ShellResultValue.Error;
		Assert.True(error is not null);
		Assert.Equal(2, fallibleTouches);
		Assert.Contains(error!.Frames, x => x.ArrayIndex == 1);
		result = Execute("outer = load_empty_markers() -> touch_marker", session);
		Completed(result);
		Assert.True(session.TryGetBinding("outer", out var outer));
		Assert.True(outer.Value is ShellResultValue.VoidSuccess);
		result = Execute("x = \"old\"", session);
		Completed(result);
		faultTouches = 0;
		result = Execute("x = find_entities(classname: \"info_spawn\") -> fault_marker\nx = \"new\"", session);
		Assert.Equal(ExecutionStatus.RuntimeFault, result.Status);
		Assert.Equal(0, result.CompletedStatementCount);
		Assert.Equal("GAME1001", result.RuntimeFault!.Code.Value);
		Assert.Equal(1, result.RuntimeFault.Context.Count(x => x.ArrayIndex is not null));
		Assert.Equal(1, result.RuntimeFault.Context.Single(x => x.ArrayIndex is not null).ArrayIndex!.Value);
		Assert.True(session.TryGetBinding("x", out var old));
		Assert.Equal("old", old.Get<string>());
		result = Execute("find_entities(classname: \"info_spawn\") -> explode_marker\nafter = 1", session);
		Assert.Equal(ExecutionStatus.HostFault, result.Status);
		Assert.True(!session.TryGetBinding("after", out _));
		result = Execute("find_entities(classname: \"info_spawn\") -> undeclared_fault", session);
		Assert.Equal(ExecutionStatus.HostFault, result.Status);
		result = Execute("find_entities(classname: \"info_spawn\") -> spawn_monster(director <- encounter_director, difficulty: Hard, faction: Hostile, reason: MapStart, seed: 1, start_awake: false)", session);
		Assert.Equal(ExecutionStatus.RuntimeFault, result.Status);
		Assert.Equal("GAME1001", result.RuntimeFault!.Code.Value);
		Assert.Equal(0, game.SpawnedMonsters);
		result = Execute("wrong_output()", session);
		Assert.Equal(ExecutionStatus.HostFault, result.Status);
		result = Execute("([1, 2, 3] + 1) -> sum", session);
		Completed(result);
		Assert.Equal(9, result.Value!.Get<int>());
		result = Execute("((find_entities(classname: \"info_spawn\") -> first).spawn_order + 1) -> require", session);
		Completed(result);
		Assert.Equal(2, result.Value!.Get<int>());
		result = Execute("(1 + (find_entities(classname: \"missing\") -> first).spawn_order) -> is_ok", session);
		Completed(result);
		Assert.False(result.Value!.Get<bool>());
		result = Execute("(false && fail_bool()) -> require", session);
		Completed(result);
		Assert.False(result.Value!.Get<bool>());
		Assert.Equal(0, boolCalls);
		const string spawn = "find_entities(classname: \"info_spawn\") -> choose_random_spawn(seed: 1) -> require -> spawn_player(player <- local_player, world <- world, facing: MarkerAngles, protection_seconds: 1.0) -> require";
		result = Execute(spawn + " -> inspect_spawn_output", session);
		Completed(result);
		Assert.True(result.Value!.Get<bool>());
		result = Execute(spawn + " -> accept_spawned_player", session);
		Completed(result);
		Assert.True(result.Value!.Get<bool>());
		Assert.True(!engine.Compile("local_player.password", session).IsValid, "Unregistered CLR members must be hidden.");
		Assert.Throws<ArgumentException>(() => engine.Catalog.ArrayOf(core.Void));
		var require = engine.Catalog.Intrinsics.Single(x => x.Name == "require");
		Assert.Equal("intrinsic", engine.GetHelp(require.Id)!.Kind);

		ExecutionResult Execute(string source, ShellSession target)
		{
			var compilation = engine.Compile(source, target);
			if (!compilation.IsValid)
				throw new InvalidOperationException(string.Join(Environment.NewLine, compilation.Diagnostics));
			return engine.Execute(compilation, target);
		}
	}

	private static void Good(RegistrationResult result)
	{
		if (!result.Success)
			throw new InvalidOperationException(string.Join(Environment.NewLine, result.Diagnostics));
	}
	private static void Completed(ExecutionResult result)
	{
		if (result.Status != ExecutionStatus.Completed)
			throw new InvalidOperationException(result.HostFault?.Message ?? result.RuntimeFault?.Message);
	}

}
