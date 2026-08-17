using ShellLang;
using ShellLangTest;
using Xunit;

public sealed class RoadmapTests
{
	[Fact]
	public void IntrinsicSchemaAndFlatten()
	{
		var engine = Fixture();
		var session = new ShellSession();
		Assert.Equal(26, engine.Catalog.Intrinsics.Count);
		var signatures = engine.Catalog.Intrinsics.ToDictionary(x => x.Name,
			x => engine.GetHelp(x.Id)!.Signatures.ToArray(), StringComparer.Ordinal);
		var expected = new Dictionary<string, string[]>(StringComparer.Ordinal)
		{
			["require"] = ["Result<T,E> -> T"],
			["value_or"] = ["Result<T,E>, default: T -> T"],
			["error"] = ["Result<T,E> -> E"],
			["is_ok"] = ["Result<T,E> -> Bool"],
			["where"] = ["Array<T>, predicate: T -> Bool -> Array<T>"],
			["sort"] = ["Array<T>, by: T -> K -> Array<T>"],
			["take"] = ["Array<T>, count: Int32 -> Array<T>"],
			["count"] = ["Array<T> -> Int32"],
			["sum"] = ["Array<T> -> T"],
			["first"] = ["Array<T> -> Result<T,EmptyCollectionError>"],
			["min"] = ["Array<T> -> Result<T,EmptyCollectionError>"],
			["max"] = ["Array<T> -> Result<T,EmptyCollectionError>"],
			["average"] = ["Array<T> -> Result<Average<T>,EmptyCollectionError>"],
			["at"] = ["Array<T>, index: Int32 -> T"],
			["last"] = ["Array<T> -> Result<T,EmptyCollectionError>"],
			["skip"] = ["Array<T>, count: Int32 -> Array<T>"],
			["slice"] = ["Array<T>, start: Int32, count: Int32 -> Array<T>"],
			["any"] = ["Array<T>, predicate: T -> Bool -> Bool"],
			["all"] = ["Array<T>, predicate: T -> Bool -> Bool"],
			["select"] = ["Array<T>, selector: T -> K -> Array<K>"],
			["contains"] = ["Array<T>, value: T -> Bool"],
			["concat"] = ["Array<T>, other: Array<T> -> Array<T>"],
			["distinct"] = ["Array<T> -> Array<T>", "Array<T>, by: T -> K -> Array<T>"],
			["reverse"] = ["Array<T> -> Array<T>"],
			["single"] = ["Array<T> -> Result<T,CollectionCardinalityError>"],
			["flatten"] = ["Array<Array<T>> -> Array<T>"]
		};
		Assert.Equal(expected.Keys.Order(), signatures.Keys.Order());
		foreach (var pair in expected)
			Assert.Equal(pair.Value, signatures[pair.Key]);
		var flatten = engine.Catalog.Intrinsics.Single(x => x.Name == "flatten");
		Assert.Equal(IntrinsicPrimaryShape.NestedArray, flatten.PrimaryShape);
		Assert.Equal(IntrinsicConstraintKind.None, flatten.Signatures.Single().Constraint);
		Assert.True(((System.Collections.IList)flatten.Signatures.Single().Parameters).IsReadOnly);
		var help = engine.GetHelp(flatten.Id);
		Assert.Equal(["Array<Array<T>> -> Array<T>"], help!.Signatures);
		Assert.Equal(IntrinsicPrimaryShape.NestedArray, help.IntrinsicPrimaryShape);
		Assert.Same(flatten.Signatures.Single(), help.IntrinsicSignatures.Single());

		var result = Execute(engine, session, "[[1, 2], [], [3, 4]] -> flatten");
		Assert.Equal("[1, 2, 3, 4]", engine.FormatValue(result.Value!, session));
		Assert.Equal(engine.Catalog.ArrayOf(engine.Core.Int32), result.Value!.Type);
		Assert.Equal(4, engine.GetArrayItems(result.Value).Count);
		var emptyNested = engine.CreateArray(engine.Catalog.ArrayOf(engine.Core.Int32), []);
		session.SetBinding("empty_nested", emptyNested);
		result = Execute(engine, session, "empty_nested -> flatten");
		Assert.Empty(engine.GetArrayItems(result.Value!));

		result = Execute(engine, session, "[[[1]], [[2, 3]]] -> flatten");
		Assert.Equal("[[1], [2, 3]]", engine.FormatValue(result.Value!, session));
		Assert.Equal(engine.Catalog.ArrayOf(engine.Catalog.ArrayOf(engine.Core.Int32)), result.Value!.Type);

		result = Execute(engine, session, "[[[1]], [[2]]] -> first -> flatten");
		Assert.Equal("Ok([1])", engine.FormatValue(result.Value!, session));
		Assert.False(engine.Compile("[1, 2] -> flatten", session).IsValid);
		var flattenCompletion = Assert.Single(engine.GetCompletions("[[1]] -> fla", 12, session).Items,
			x => x.InsertionText == "flatten");
		Assert.Same(flatten.Signatures.Single(), flattenCompletion.IntrinsicSignatures!.Single());
		Assert.DoesNotContain(engine.GetCompletions("[1] -> fla", 10, session).Items,
			x => x.InsertionText == "flatten");

		var nestedType = engine.Catalog.ArrayOf(engine.Catalog.ArrayOf(engine.Core.Int32));
		var command = new CommandDescriptor("nested_output", "Create nested output.", null, null,
			[new OutputPortDescriptor("nested", "Nested values.", nestedType, true),
				new OutputPortDescriptor("label", "Label.", engine.Core.String)],
			(_, _) => new CommandOutcome.Success(new Dictionary<string, ShellValue>
			{
				["nested"] = engine.CreateArray(engine.Catalog.ArrayOf(engine.Core.Int32),
					[engine.CreateArray(engine.Core.Int32, [engine.CreateValue(engine.Core.Int32, 7)])]),
				["label"] = engine.CreateValue(engine.Core.String, "nested")
			}));
		Assert.True(engine.Register(new DescriptorSet(commands: [command])).Success);
		result = Execute(engine, session, "nested_output() -> flatten");
		Assert.Equal("[7]", engine.FormatValue(result.Value!, session));
	}

	[Fact]
	public void FlattenPreservesDeclaredCovariantElementType()
	{
		var engine = new ShellEngine();
		var baseType = TypeDescriptorBuilder.For<BaseActor>("FlattenBase").Build();
		var derivedType = TypeDescriptorBuilder.For<DerivedActor>("FlattenDerived").Base(baseType.Id).Build();
		Assert.True(engine.Register(new DescriptorSet(types: [baseType, derivedType])).Success);
		var derived = engine.CreateValue(derivedType.Id, new DerivedActor("derived"));
		var inner = engine.CreateArray(baseType.Id, [derived]);
		var outer = engine.CreateArray(engine.Catalog.ArrayOf(baseType.Id), [inner]);
		var session = new ShellSession();
		session.SetBinding("nested", outer);

		var result = Execute(engine, session, "nested -> flatten");
		Assert.Equal(engine.Catalog.ArrayOf(baseType.Id), result.Value!.Type);
		Assert.Same(derived, engine.GetArrayItems(result.Value).Single());
		Assert.Single(engine.GetArrayItems(inner));
		var completion = engine.GetCompletions("nested -> ", 10, session).Items;
		Assert.DoesNotContain(completion, x => x.InsertionText is "sum" or "average" or "min" or "max" or "contains");
		Assert.Contains(completion, x => x.InsertionText == "count");
	}

	[Fact]
	public void QualifiedCommandsAliasesWarningsAndDiscoveryMetadata()
	{
		var engine = new ShellEngine();
		var core = engine.Core;
		CommandDescriptor Echo(string commandNamespace, string alias) => new("echo", "Echo an integer.",
			[new InputPortDescriptor("value", "Value.", core.Int32, true)], null,
			[new OutputPortDescriptor("value", "Value.", core.Int32, true)],
			(_, values) => CommandOutcome.Success.Single("value", values.GetInput("value")),
			namespaceName: commandNamespace, category: "testing", examples: [$"1 -> {commandNamespace}::echo"],
			aliases: [new(alias, new("Use the qualified spelling.", "0.1", $"{commandNamespace}::echo"))],
			introducedVersion: "0.1");
		var retired = new CommandDescriptor("retired", "Deprecated command.", null, null, null,
			(_, _) => CommandOutcome.Success.Empty, namespaceName: "alpha",
			deprecation: new CommandDeprecation("This command is retired.", "0.1", "alpha::echo"));
		var namespaces = new[]
		{
			new CommandNamespaceDescriptor("alpha", "Alpha commands."),
			new CommandNamespaceDescriptor("beta", "Beta commands."),
			new CommandNamespaceDescriptor("alpha::nested", "Nested alpha commands.")
		};
		var registration = engine.Register(new DescriptorSet(commands: [Echo("alpha", "old_echo"), Echo("beta", "beta_echo"), retired],
			commandNamespaces: namespaces));
		Assert.True(registration.Success, string.Join(Environment.NewLine, registration.Diagnostics));
		Assert.Equal(2, engine.Catalog.Commands.Count(x => x.Name == "echo"));
		Assert.Equal(3, engine.Catalog.CommandNamespaces.Count);

		var session = new ShellSession();
		session.SetBinding("echo", engine.CreateValue(core.String, "binding"));
		var canonical = engine.Compile("1 -> alpha::echo", session);
		Assert.True(canonical.IsValid);
		Assert.Empty(canonical.Diagnostics);
		Assert.Equal(1, engine.Execute(canonical, session).Value!.Get<int>());

		var alias = engine.Compile("1 -> old_echo", session);
		Assert.True(alias.IsValid);
		var warning = Assert.Single(alias.Diagnostics);
		Assert.Equal("SL2601", warning.Code);
		Assert.Equal(DiagnosticSeverity.Warning, warning.Severity);
		Assert.Equal("alpha::echo", warning.SymbolName);
		Assert.Equal(ExecutionStatus.Completed, engine.Execute(alias, session).Status);
		var canonicalDeprecation = engine.Compile("alpha::retired()", session);
		Assert.True(canonicalDeprecation.IsValid);
		Assert.Equal("SL2601", Assert.Single(canonicalDeprecation.Diagnostics).Code);
		Assert.Equal(ExecutionStatus.Completed, engine.Execute(canonicalDeprecation, session).Status);

		var completion = engine.GetCompletions("alpha::e", 8, session).Items;
		var item = Assert.Single(completion, x => x.InsertionText == "alpha::echo");
		Assert.Equal("testing", item.Category);
		Assert.Equal("alpha::echo", item.CanonicalName);
		Assert.Equal("alpha", item.Namespace);
		Assert.DoesNotContain(completion, x => x.InsertionText == "old_echo");
		var retiredCompletion = Assert.Single(engine.GetCompletions("alpha::r", 8, session).Items,
			x => x.InsertionText == "alpha::retired");
		Assert.True(retiredCompletion.IsDeprecated);
		Assert.Equal("This command is retired.", retiredCompletion.Deprecation!.Message);
		var command = engine.Catalog.Commands.Single(x => x.QualifiedName == "alpha::echo");
		var help = engine.GetHelp(command.Id)!;
		Assert.Equal("alpha", help.Namespace);
		Assert.Equal("testing", help.Category);
		Assert.Equal("0.1", help.IntroducedVersion);
		Assert.Equal("old_echo", Assert.Single(help.Aliases).Name);
		Assert.Single(help.Examples);
		Assert.True(((System.Collections.IList)command.Examples).IsReadOnly);
	}

	[Fact]
	public void CallableCollisionsAndUnknownNamespaceRejectAtomically()
	{
		var engine = new ShellEngine();
		var revision = engine.CatalogRevision;
		var command = new CommandDescriptor("probe", "Probe.", null, null, null,
			(_, _) => CommandOutcome.Success.Empty, aliases: [new CommandAliasDescriptor("count")]);
		var collision = engine.Register(new DescriptorSet(commands: [command]));
		Assert.False(collision.Success);
		Assert.Contains(collision.Diagnostics, x => x.Code == "SL3023");
		Assert.Equal(revision, engine.CatalogRevision);
		Assert.Empty(engine.Catalog.Commands);

		var namespaced = new CommandDescriptor("probe", "Probe.", null, null, null,
			(_, _) => CommandOutcome.Success.Empty, namespaceName: "missing");
		var unknown = engine.Register(new DescriptorSet(commands: [namespaced]));
		Assert.False(unknown.Success);
		Assert.Contains(unknown.Diagnostics, x => x.Code == "SL3025");
		Assert.Equal(revision, engine.CatalogRevision);
	}

	[Fact]
	public void RegisteredCommandExamplesCompileWithoutWarnings()
	{
		var engine = Fixture();
		var examples = engine.Catalog.Commands.SelectMany(command => command.Examples
			.Select(source => (command.QualifiedName, Source: source))).ToArray();
		Assert.NotEmpty(examples);
		foreach (var example in examples)
		{
			var compilation = engine.Compile(example.Source, new ShellSession());
			Assert.True(compilation.IsValid,
				$"{example.QualifiedName}: {string.Join(Environment.NewLine, compilation.Diagnostics)}");
			Assert.Empty(compilation.Diagnostics);
		}
	}

	private static ShellEngine Fixture()
	{
		var engine = new ShellEngine();
		var registration = new MockGame().Register(engine);
		Assert.True(registration.Success, string.Join(Environment.NewLine, registration.Diagnostics));
		return engine;
	}

	private static ExecutionResult Execute(ShellEngine engine, ShellSession session, string source)
	{
		var compilation = engine.Compile(source, session);
		Assert.True(compilation.IsValid, string.Join(Environment.NewLine, compilation.Diagnostics));
		var result = engine.Execute(compilation, session);
		Assert.Equal(ExecutionStatus.Completed, result.Status);
		return result;
	}
}
