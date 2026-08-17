using ShellLang;

namespace ShellLangTest;

internal static class ValueFormattingConformance
{
    public static void Run()
    {
        var printed = new List<string>();
        var engine = new ShellEngine();
        var game = new MockGame(printed.Add);
        Good(game.Register(engine));
        var core = engine.Core;
        var queryCalls = 0;

        var baseType = new TypeDescriptor("FormattingBase", "Formatting base.", typeof(FormattingBase),
            new ValueAdapter<FormattingBase>(), members:
            [
                Member<FormattingBase, string>("base", core.String, (context, value) => value.Base, engine),
                Member<FormattingBase, string>("shadow", core.String, (context, value) => "base-shadow", engine)
            ]);
        var derivedType = new TypeDescriptor("FormattingDerived", "Formatting derived.", typeof(FormattingDerived),
            new ValueAdapter<FormattingDerived>(), [baseType.Id],
            [
                Member<FormattingDerived, string>("derived", core.String, (context, value) => value.Derived, engine),
                Member<FormattingDerived, string>("shadow", core.String, (context, value) => "derived-shadow", engine)
            ],
            [new QueryDescriptor("hidden_query", "Must not run during formatting.", default, null, core.String,
                (context, receiver, values) =>
                {
                    queryCalls++;
                    return new QueryOutcome.Success(context.Engine.CreateValue(core.String, "query-leak"));
                })]);

        var opaqueType = new TypeDescriptor("FormattingOpaque", "Opaque formatter value.", typeof(FormattingOpaque),
            new ValueAdapter<FormattingOpaque>());
        var nodeType = new TypeDescriptor("FormattingNode", "Recursive formatter node.", typeof(FormattingNode),
            new ValueAdapter<FormattingNode>(), members:
            [
                new MemberDescriptor("name", "Node name.", default, core.String, (context, receiver) =>
                    context.Engine.CreateValue(core.String, receiver.Get<FormattingNode>().Name)),
                new MemberDescriptor("next", "Next node.", default, core.Any, (context, receiver) =>
                {
                    var next = receiver.Get<FormattingNode>().Next;
                    return next is null ? null! : context.Engine.CreateValue(receiver.Type, next);
                }),
                new MemberDescriptor("path", "Array index path.", default, core.String, (context, receiver) =>
                    context.Engine.CreateValue(core.String, string.Concat(context.ArrayIndexPath.Select(index => $"[{index}]"))))
            ]);

        var toggledAdapter = new ToggleAdapter();
        var toggledType = new TypeDescriptor("FormattingToggle", "Toggle-valid value.", typeof(FormattingToggle), toggledAdapter);
        ShellValue adapterInvalid = null!;
        var failureType = new TypeDescriptor("FormattingFailures", "Formatter failure probes.", typeof(FormattingFailures),
            new ValueAdapter<FormattingFailures>(), members:
            [
                new MemberDescriptor("healthy", "Healthy member.", default, core.Int32, (context, receiver) =>
                    context.Engine.CreateValue(core.Int32, 7)),
                new MemberDescriptor("throws", "Throwing member.", default, core.String, (context, receiver) =>
                    throw new InvalidOperationException("getter secret")),
                new MemberDescriptor("null_value", "Null member.", default, core.String, (context, receiver) => null!),
                new MemberDescriptor("wrong_type", "Wrong type member.", default, core.String, (context, receiver) =>
                    context.Engine.CreateValue(core.Int32, 9)),
                new MemberDescriptor("adapter_invalid", "Adapter-invalid member.", default, toggledType.Id, (context, receiver) => adapterInvalid),
                new MemberDescriptor("session_value", "Session-backed member.", default, core.Int32, (context, receiver) =>
                    context.Session.TryGetBinding("format_session_value", out var value) ? value : context.Engine.CreateValue(core.Int32, -1))
            ]);

        var pair = new CommandDescriptor("format_pair", "Create a formatting output record.", null, null,
            [
                new OutputPortDescriptor("number", "Number.", core.Int32, true),
                new OutputPortDescriptor("text", "Text.", core.String)
            ],
            (context, values) => new CommandOutcome.Success(new Dictionary<string, ShellValue>(StringComparer.Ordinal)
            {
                ["number"] = context.Engine.CreateValue(core.Int32, 1),
                ["text"] = context.Engine.CreateValue(core.String, "two")
            }));

        Good(engine.Register(new DescriptorSet(types: [baseType, derivedType, opaqueType, nodeType, toggledType, failureType], commands: [pair])));
        var session = new ShellSession();
        session.SetBinding("format_session_value", engine.CreateValue(core.Int32, 42));

        Equal("\"a\\n\\t\\u0001\\\"\\\\\"", Format(engine.CreateValue(core.String, "a\n\t\u0001\"\\")));
        Equal("true", Format(engine.CreateValue(core.Bool, true)));
        Equal("-12", Format(engine.CreateValue(core.Int32, -12)));
        Equal("1.2345679", Format(engine.CreateValue(core.Float32, 1.2345679F)));
        Equal("1.2345678901234567", Format(engine.CreateValue(core.Float64, 1.2345678901234567D)));
        var difficulty = engine.Catalog.Enums.Single(type => type.Name == "Difficulty");
        Equal("Hard", Format(engine.CreateValue(difficulty.Id, Difficulty.Hard)));

        var inner = engine.CreateArray(core.Any,
            [engine.CreateValue(core.Int32, 1), engine.CreateValue(core.String, "two")]);
        var nested = engine.CreateArray(inner.Type, [inner]);
        Equal("[[1, \"two\"]]", Format(nested));
        Equal("Ok(5)", Format(engine.CreateSuccess(core.Int32, core.Error, engine.CreateValue(core.Int32, 5))));
        Equal("Ok", Format(engine.CreateVoidSuccess(core.Error)));
        Equal("Err(Error)", Format(engine.CreateError(core.Int32, core.Error,
            engine.CreateValue(core.Error, new ShellError("must-not-leak")))));

        var output = Execute("format_pair()").Value!;
        Equal("FormatPair.Output { number: 1, text: \"two\" }", Format(output));

        var derived = engine.CreateValue(derivedType.Id, new FormattingDerived("base-value", "derived-value"));
        var derivedText = Format(derived);
        Equal("FormattingDerived { derived: \"derived-value\", shadow: \"derived-shadow\", base: \"base-value\" }", derivedText);
        True(!derivedText.Contains("SECRET", StringComparison.Ordinal));
        True(!derivedText.Contains("query", StringComparison.Ordinal));
        Equal(0, queryCalls);
        Equal("FormattingOpaque", Format(engine.CreateValue(opaqueType.Id, new FormattingOpaque("OPAQUE-SECRET"))));

        var self = new FormattingNode("self");
        self.Next = self;
        Equal("FormattingNode { name: \"self\", next: <cycle: FormattingNode>, path: \"\" }",
            Format(engine.CreateValue(nodeType.Id, self)));
        var shallowCycle = engine.FormatValue(engine.CreateValue(nodeType.Id, self), session,
            new ValueFormatOptions { MaxDepth = 1 });
        True(shallowCycle.Contains("next: <cycle: FormattingNode>", StringComparison.Ordinal));
        True(!shallowCycle.Contains("<max-depth:", StringComparison.Ordinal));

        var first = new FormattingNode("first");
        var second = new FormattingNode("second");
        first.Next = second;
        second.Next = first;
        var cycle = Format(engine.CreateValue(nodeType.Id, first));
        True(cycle.Contains("next: FormattingNode { name: \"second\", next: <cycle: FormattingNode>", StringComparison.Ordinal));

        var terminal = engine.CreateValue(nodeType.Id, new FormattingNode("shared"));
        var shared = Format(engine.CreateArray(nodeType.Id, [terminal, terminal]));
        Equal(0, Count(shared, "<cycle:"));
        Equal(2, Count(shared, "FormattingNode {"));
        True(shared.Contains("path: \"[0]\"", StringComparison.Ordinal));
        True(shared.Contains("path: \"[1]\"", StringComparison.Ordinal));

        var chain = new FormattingNode("0");
        var cursor = chain;
        for (var i = 1; i <= 9; i++)
        {
            cursor.Next = new FormattingNode(i.ToString(System.Globalization.CultureInfo.InvariantCulture));
            cursor = cursor.Next;
        }
        var defaultDepth = Format(engine.CreateValue(nodeType.Id, chain));
        True(defaultDepth.Contains("<max-depth: FormattingNode>", StringComparison.Ordinal));
        var shallow = engine.FormatValue(engine.CreateValue(nodeType.Id, chain), session, new ValueFormatOptions { MaxDepth = 1 });
        True(shallow.Contains("next: <max-depth: FormattingNode>", StringComparison.Ordinal));
        Throws<ArgumentOutOfRangeException>(() => engine.FormatValue(derived, session, new ValueFormatOptions { MaxDepth = 0 }));

        toggledAdapter.Enabled = true;
        adapterInvalid = engine.CreateValue(toggledType.Id, new FormattingToggle());
        toggledAdapter.Enabled = false;
        var failures = Format(engine.CreateValue(failureType.Id, new FormattingFailures()));
        Equal("FormattingFailures { healthy: 7, throws: <unavailable: String>, null_value: <unavailable: String>, wrong_type: <unavailable: String>, adapter_invalid: <unavailable: FormattingToggle>, session_value: 42 }", failures);
        True(!failures.Contains("getter secret", StringComparison.Ordinal));

        session.SetBinding("formatted", derived);
        printed.Clear();
        var printResult = Execute("formatted -> print");
        Equal(ExecutionStatus.Completed, printResult.Status);
        True(printResult.Value is null);
        Equal(derivedText, printed.Single());
        printed.Clear();
        Execute("\"Hello world\" -> print");
        Equal("\"Hello world\"", printed.Single());
        Equal(0, queryCalls);

        string Format(ShellValue value) => engine.FormatValue(value, session);
        ExecutionResult Execute(string source)
        {
            var compilation = engine.Compile(source, session);
            if (!compilation.IsValid)
                throw new InvalidOperationException(string.Join(Environment.NewLine, compilation.Diagnostics));
            return engine.Execute(compilation, session);
        }
    }

    private static MemberDescriptor Member<TReceiver, TValue>(string name, ShellTypeId type,
        Func<InvocationContext, TReceiver, TValue> getter, ShellEngine engine)
        where TReceiver : notnull where TValue : notnull =>
        new(name, $"Formatting {name}.", default, type, (context, receiver) =>
            engine.CreateValue(type, getter(context, receiver.Get<TReceiver>())!));

    private static int Count(string value, string text)
    {
        var count = 0;
        for (var index = 0; (index = value.IndexOf(text, index, StringComparison.Ordinal)) >= 0; index += text.Length)
            count++;
        return count;
    }

    private static void Good(RegistrationResult result)
    {
        if (!result.Success)
            throw new InvalidOperationException(string.Join(Environment.NewLine, result.Diagnostics));
    }

    private static void True(bool value)
    {
        if (!value)
            throw new InvalidOperationException("Expected true.");
    }

    private static void Equal<T>(T expected, T actual) where T : notnull
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"Expected {expected}, received {actual}.");
    }

    private static void Throws<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new InvalidOperationException($"Expected {typeof(T).Name}.");
    }

    private class FormattingBase(string baseValue)
    {
        public string Base { get; } = baseValue;
    }

    private sealed class FormattingDerived(string baseValue, string derivedValue) : FormattingBase(baseValue)
    {
        public string Derived { get; } = derivedValue;
        public string Secret => "SECRET-PROPERTY";
        public override string ToString() => "SECRET-TOSTRING";
    }

    private sealed record FormattingOpaque(string Secret)
    {
        public override string ToString() => Secret;
    }

    private sealed class FormattingNode(string name)
    {
        public string Name { get; } = name;
        public FormattingNode? Next { get; set; }
    }

    private sealed class FormattingFailures
    {
    }

    private sealed class FormattingToggle
    {
    }

    private sealed class ToggleAdapter : ValueAdapter
    {
        public bool Enabled { get; set; } = true;
        public override Type ClrType => typeof(FormattingToggle);
        public override bool IsValid(object value) => Enabled && value is FormattingToggle;
        public override object GetClrValue(ShellValue value) => value.Value;
        public override ShellValue CreateShellValue(object value) => throw new NotSupportedException();
    }
}
