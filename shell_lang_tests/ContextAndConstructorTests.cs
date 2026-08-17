using ShellLang;
using ShellLangTest;
using Xunit;

public sealed class ContextAndConstructorTests
{
	[Fact]
	public void ContextualThisMatchesLeadingDotAndShadowsInQueries()
	{
		var (engine, session) = Fixture();
		var explicitThis = Run(engine, session,
			"find_entities(classname: \"info_spawn\") -> where(this.spawn_order > 1) -> count");
		var leadingDot = Run(engine, session,
			"find_entities(classname: \"info_spawn\") -> where(.spawn_order > 1) -> count");
		Assert.Equal(2, explicitThis.Value!.Get<int>());
		Assert.Equal(2, leadingDot.Value!.Get<int>());

		var lifted = Run(engine, session,
			"[local_player, local_player] -> give_credits(amount: this.name_length()) -> require -> count");
		Assert.Equal(2, lifted.Value!.Get<int>());
	}

	[Fact]
	public void ContextIsRejectedOutsideAnEffectivePrimary()
	{
		var (engine, session) = Fixture();
		var topLevel = engine.Compile("this", session);
		Assert.Contains(topLevel.Diagnostics, x => x.Code == "SL2308" && x.ContextType is null);
		var standalone = engine.Compile("give_credits(amount: this.name_length())", session);
		Assert.Contains(standalone.Diagnostics, x => x.Code == "SL2308");
	}

	[Fact]
	public void ExactArrayAndLiftedScalarContextsUseDifferentThisTypes()
	{
		var engine = new ShellEngine();
		var core = engine.Core;
		var array = engine.Catalog.ArrayOf(core.Int32);
		var count = new CommandDescriptor("assert_count", "Checks an exact array context.",
			[new InputPortDescriptor("items", "Items.", array, true)],
			[new ArgumentDescriptor("count", "Count.", core.Int32, 0)],
			[new OutputPortDescriptor("value", "Count.", core.Int32, true)],
			(_, values) => CommandOutcome.Success.Single("value", values.GetArgument("count")));
		Assert.True(engine.Register(new DescriptorSet(commands: [count])).Success);
		var session = new ShellSession();
		var compilation = engine.Compile("[1, 2, 3] -> assert_count(count: this -> count)", session);
		Assert.True(compilation.IsValid, Diagnostics(compilation));
		Assert.Equal(3, engine.Execute(compilation, session).Value!.Get<int>());
		Assert.Equal(3, Run(engine, session, "[1, 2, 3] -> take(count: this -> count) -> count").Value!.Get<int>());
	}

	[Fact]
	public void ContextualSecondariesRunPerLeafWhileOrdinarySecondariesAreHoisted()
	{
		var engine = new ShellEngine();
		var core = engine.Core;
		var ticks = 0;
		var tick = new CommandDescriptor("tick", "Returns a counted value.", null, null,
			[new OutputPortDescriptor("value", "Value.", core.Int32)],
			(context, _) => CommandOutcome.Success.Single("value",
				context.Engine.CreateValue(core.Int32, ++ticks)));
		var add = new CommandDescriptor("add", "Adds an argument.",
			[new InputPortDescriptor("value", "Value.", core.Int32, true)],
			[new ArgumentDescriptor("amount", "Amount.", core.Int32, 0)],
			[new OutputPortDescriptor("value", "Value.", core.Int32)],
			(context, values) => CommandOutcome.Success.Single("value", context.Engine.CreateValue(core.Int32,
				values.GetInput<int>("value") + values.GetArgument<int>("amount"))));
		var combine = new CommandDescriptor("combine", "Combines explicit inputs.",
			[new InputPortDescriptor("left", "Left.", core.Int32, true),
				new InputPortDescriptor("right", "Right.", core.Int32)], null,
			[new OutputPortDescriptor("value", "Value.", core.Int32)],
			(context, values) => CommandOutcome.Success.Single("value", context.Engine.CreateValue(core.Int32,
				values.GetInput<int>("left") + values.GetInput<int>("right"))));
		var amount = TypeDescriptorBuilder.For<int>("Amount").Description("Contextual amount.")
			.Member("value", "Value.", core.Int32, value => value)
			.Constructor([new ArgumentDescriptor("value", "Value.", core.Int32, 0)],
				(_, values) => values.GetArgument<int>("value"))
			.Build();
		Assert.True(engine.Register(new DescriptorSet(types: [amount], commands: [tick, add, combine])).Success);
		var session = new ShellSession();

		Assert.Equal(9, Run(engine, session, "[1, 2, 3] -> add(amount: tick()) -> sum").Value!.Get<int>());
		Assert.Equal(1, ticks);
		ticks = 0;
		Assert.Equal(18, Run(engine, session, "[1, 2, 3] -> add(amount: tick() + this) -> sum").Value!.Get<int>());
		Assert.Equal(3, ticks);
		Assert.Equal(12, Run(engine, session, "[1, 2, 3] -> combine(right <- this) -> sum").Value!.Get<int>());
		Assert.Equal(12, Run(engine, session, "[1, 2, 3] -> add(amount: Amount(this).value) -> sum").Value!.Get<int>());

		var empty = engine.CreateArray(core.Int32, []);
		session.SetBinding("empty", empty);
		ticks = 0;
		Assert.Equal(0, Run(engine, session, "empty -> add(amount: tick() + this) -> sum").Value!.Get<int>());
		Assert.Equal(0, ticks);
		Assert.Equal(0, Run(engine, session, "empty -> add(amount: tick()) -> sum").Value!.Get<int>());
		Assert.Equal(1, ticks);

		var failed = engine.CreateError(engine.Catalog.ArrayOf(core.Int32), core.Error,
			engine.CreateValue(core.Error, new ShellError("stop")));
		session.SetBinding("failed", failed);
		ticks = 0;
		var propagated = Run(engine, session, "failed -> add(amount: tick())");
		Assert.IsType<ShellResultValue.Error>(propagated.Value!.Value);
		Assert.Equal(0, ticks);
	}

	[Fact]
	public void ConstructorsSupportDefaultsNestingMembersAndArrays()
	{
		var (engine, session) = Fixture();
		Assert.Equal(1F, Run(engine, session, "Vector3(1, 2, 3).x").Value!.Get<float>());
		Assert.Equal(1F, Run(engine, session, "Color(r: 0, g: 0, b: 0).a").Value!.Get<float>());
		Assert.Equal(2F, Run(engine, session,
			"Transform(Vector3(1, 2, 3), Quaternion(), Vector3(1, 1, 1)).position.y").Value!.Get<float>());
		Assert.Equal(3F, Run(engine, session,
			"[Vector3(1, 2, 3)] -> select(this.z) -> first -> require").Value!.Get<float>());
		Assert.Contains(engine.Compile("Player()", session).Diagnostics, x => x.Code == "SL2501");
		Assert.Contains(engine.Compile("local_player -> Vector3(1, 2, 3)", session).Diagnostics,
			x => x.Code == "SL2502");
		Assert.Contains(engine.Compile("Vector3(x <- 1, 2, 3)", session).Diagnostics,
			x => x.Code == "SL2503");
	}

	[Fact]
	public void ConstructorFailuresAndRegistrationAreContainedAndAtomic()
	{
		var engine = new ShellEngine();
		var core = engine.Core;
		var error = new ErrorTypeDescriptor("BuildError", "Build error.", typeof(ShellError),
			new ValueAdapter<ShellError>(), core.Error);
		var widget = TypeDescriptorBuilder.For<int>("Widget").Description("Widget.")
			.FallibleConstructor([
				new ArgumentDescriptor("value", "Value.", core.Int32, 0)], error.Id,
				(context, values) => values.GetArgument<int>("value") < 0
					? new ConstructorOutcome<int>.Error(context.Engine.CreateValue(error.Id, new ShellError("negative")))
					: new ConstructorOutcome<int>.Success(values.GetArgument<int>("value")))
			.Build();
		Assert.True(engine.Register(new DescriptorSet(types: [widget], errors: [error])).Success);
		var session = new ShellSession();
		var failed = Run(engine, session, "Widget(-1)");
		Assert.IsType<ShellResultValue.Error>(failed.Value!.Value);
		var argumentFailed = Run(engine, session, "Widget([1] -> skip(1) -> first)");
		Assert.IsType<ShellResultValue.Error>(argumentFailed.Value!.Value);
		Assert.Equal(7, Run(engine, session, "Widget(7) -> require").Value!.Get<int>());

		var revision = engine.CatalogRevision;
		var collision = TypeDescriptorBuilder.For<int>("count").Constructor(null, (_, _) => 0).Build();
		var rejected = engine.Register(new DescriptorSet(types: [collision]));
		Assert.Contains(rejected.Diagnostics, x => x.Code == "SL3023");
		Assert.Equal(revision, engine.CatalogRevision);
		var reserved = new GlobalDescriptor("this", "Reserved.", core.Int32,
			context => context.Engine.CreateValue(core.Int32, 1));
		Assert.Contains(engine.Register(new DescriptorSet(globals: [reserved])).Diagnostics, x => x.Code == "SL3022");
	}

	[Fact]
	public void ConstructorHostFaultsAndSessionMutationAreContained()
	{
		var engine = new ShellEngine();
		var core = engine.Core;
		var mutating = new ConstructorDescriptor(null, (context, _) =>
		{
			context.Session.SetBinding("changed", context.Engine.CreateValue(core.Int32, 1));
			return new ConstructorOutcome.Success(context.Engine.CreateValue(core.Int32, 1));
		});
		var type = new TypeDescriptor("Mutating", "Mutating constructor.", typeof(int),
			new ValueAdapter<int>(), constructor: mutating);
		Assert.True(engine.Register(new DescriptorSet(types: [type])).Success);
		var session = new ShellSession();
		var mutation = engine.Execute(engine.Compile("Mutating()", session), session);
		Assert.Equal(ExecutionStatus.HostFault, mutation.Status);
		Assert.Equal("SL5117", mutation.HostFault!.Code);
		Assert.False(session.TryGetBinding("changed", out _));

		var invalidConstructor = new ConstructorDescriptor(null,
			(context, _) => new ConstructorOutcome.Success(context.Engine.CreateValue(core.Int32, 1)));
		var invalid = new TypeDescriptor("Invalid", "Invalid constructor.", typeof(string),
			new ValueAdapter<string>(), constructor: invalidConstructor);
		Assert.True(engine.Register(new DescriptorSet(types: [invalid])).Success);
		var invalidResult = engine.Execute(engine.Compile("Invalid()", session), session);
		Assert.Equal(ExecutionStatus.HostFault, invalidResult.Status);
		Assert.Equal("SL5118", invalidResult.HostFault!.Code);
	}

	[Fact]
	public void HelpDiagnosticsAndCompletionExposeContextAndConstructors()
	{
		var (engine, session) = Fixture();
		var command = engine.Catalog.Commands.Single(x => x.Name == "give_credits");
		Assert.Equal(command.Inputs.Single(x => x.IsDefault).Type, engine.GetHelp(command.Id)!.ContextType);
		var vector = engine.Catalog.Types.Single(x => x.Name == "Vector3");
		var typeHelp = engine.GetHelp(vector.SymbolId)!;
		Assert.Equal(3, typeHelp.Arguments.Count);
		Assert.Equal(vector.Id, typeHelp.Outputs.Single().Type);
		Assert.Contains(engine.GetCompletions("Vec", 3, session).Items,
			x => x.InsertionText == "Vector3" && x.Kind == CompletionItemKind.Type);

		const string source = "local_player -> give_credits(amount: thi";
		Assert.Contains(engine.GetCompletions(source, source.Length, session).Items,
			x => x.InsertionText == "this" && x.Kind == CompletionItemKind.Context);
		const string memberSource = "local_player -> give_credits(amount: this.na";
		Assert.Contains(engine.GetCompletions(memberSource, memberSource.Length, session).Items,
			x => x.InsertionText == "name" && x.Kind == CompletionItemKind.Member);
		Assert.DoesNotContain(engine.GetCompletions("thi", 3, session).Items,
			x => x.Kind == CompletionItemKind.Context);
		var contextualError = engine.Compile("local_player -> give_credits(amount: \"bad\")", session);
		Assert.Contains(contextualError.Diagnostics,
			x => x.Code == "SL2004" && x.ContextType == command.Inputs.Single(i => i.IsDefault).Type);
	}

	private static (ShellEngine Engine, ShellSession Session) Fixture()
	{
		var engine = new ShellEngine();
		var registration = new MockGame().Register(engine);
		Assert.True(registration.Success, string.Join(Environment.NewLine, registration.Diagnostics));
		return (engine, new ShellSession());
	}

	private static ExecutionResult Run(ShellEngine engine, ShellSession session, string source)
	{
		var compilation = engine.Compile(source, session);
		Assert.True(compilation.IsValid, Diagnostics(compilation));
		var result = engine.Execute(compilation, session);
		Assert.Equal(ExecutionStatus.Completed, result.Status);
		return result;
	}

	private static string Diagnostics(ShellCompilation compilation) =>
		string.Join(Environment.NewLine, compilation.Diagnostics);
}
