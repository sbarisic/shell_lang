using ShellLang;
using ShellLangTest;
using Xunit;

public sealed class TypeExpressionTests
{
	[Fact]
	public void NumericConversionMatrixSupportsEverySourceAndTargetPair()
	{
		var engine = new ShellEngine();
		var session = new ShellSession();
		var core = engine.Core;
		var values = new (string Name, ShellTypeId Type, object Value)[]
		{
			("Int32", core.Int32, 1), ("Int64", core.Int64, 1L),
			("UInt32", core.UInt32, 1U), ("UInt64", core.UInt64, 1UL),
			("Float32", core.Float32, 1F), ("Float64", core.Float64, 1D)
		};
		foreach (var source in values)
		{
			session.SetBinding("source", engine.CreateValue(source.Type, source.Value));
			foreach (var target in values)
			{
				var compilation = Valid(engine, session, $"{target.Name}(source)");
				var guaranteed = source.Type == target.Type ||
					source.Type == core.Int32 && (target.Type == core.Int64 || target.Type == core.Float64) ||
					source.Type == core.UInt32 && (target.Type == core.Int64 || target.Type == core.UInt64 || target.Type == core.Float64) ||
					source.Type == core.Float32 && target.Type == core.Float64;
				Assert.Equal(guaranteed ? target.Type : engine.Catalog.ResultOf(target.Type, core.ConversionError),
					compilation.ResultType);
				var result = engine.Execute(compilation, session);
				Assert.Equal(ExecutionStatus.Completed, result.Status);
				if (!guaranteed)
					Assert.IsType<ShellResultValue.Success>(result.Value!.Value);
			}
		}
	}

	[Fact]
	public void GuaranteedAndCheckedNumericConversionsHaveDeclaredResultTypes()
	{
		var engine = new ShellEngine();
		var session = new ShellSession();
		var core = engine.Core;
		Assert.Equal(core.Int64, Valid(engine, session, "Int64(1)").ResultType);
		Assert.Equal(core.Float64, Valid(engine, session, "Float64(1)").ResultType);
		Assert.Equal(engine.Catalog.ResultOf(core.Float32, core.ConversionError),
			Valid(engine, session, "Float32(1)").ResultType);
		Assert.Equal(1L, Run(engine, session, "Int64(1)").Value!.Get<long>());
		Assert.Equal(1F, Run(engine, session, "Float32(1) -> require").Value!.Get<float>());

		session.SetBinding("wide", engine.CreateValue(core.Int64, (long)int.MaxValue + 1));
		var failed = Run(engine, session, "Int32(wide)");
		var error = Assert.IsType<ShellResultValue.Error>(failed.Value!.Value).Value.Get<ConversionError>();
		Assert.Equal(core.Int64, error.SourceType);
		Assert.Equal(core.Int32, error.TargetType);
		Assert.Contains("range", error.Reason, StringComparison.OrdinalIgnoreCase);
	}

	[Fact]
	public void NumericConversionsEnforceSignIntegralityFinitenessAndExactness()
	{
		var engine = new ShellEngine();
		var session = new ShellSession();
		var core = engine.Core;
		session.SetBinding("negative", engine.CreateValue(core.Int32, -1));
		session.SetBinding("fraction", engine.CreateValue(core.Float64, 1.5));
		session.SetBinding("nan", engine.CreateValue(core.Float64, double.NaN));
		session.SetBinding("large", engine.CreateValue(core.Int64, 16_777_217L));
		session.SetBinding("exact", engine.CreateValue(core.Int64, 16_777_216L));
		AssertError(Run(engine, session, "UInt32(negative)"));
		AssertError(Run(engine, session, "Int32(fraction)"));
		AssertError(Run(engine, session, "Float32(nan)"));
		AssertError(Run(engine, session, "Float32(large)"));
		Assert.Equal(16_777_216F, Run(engine, session, "Float32(exact) -> require").Value!.Get<float>());

		session.SetBinding("max", engine.CreateValue(core.UInt64, ulong.MaxValue));
		AssertError(Run(engine, session, "Float64(max)"));
		session.SetBinding("infinity", engine.CreateValue(core.Float32, float.PositiveInfinity));
		Assert.True(double.IsPositiveInfinity(Run(engine, session, "Float64(infinity)").Value!.Get<double>()));
		Assert.True(float.IsPositiveInfinity(Run(engine, session, "Float32(infinity)").Value!.Get<float>()));
	}

	[Fact]
	public void StringConversionsAreInvariantAndUseShellEnumNames()
	{
		var (engine, session) = Fixture();
		var core = engine.Core;
		session.SetBinding("number", engine.CreateValue(core.Float64, 12.5));
		Assert.Equal("true", Run(engine, session, "String(true)").Value!.Get<string>());
		Assert.Equal("12.5", Run(engine, session, "String(number)").Value!.Get<string>());
		Assert.Equal("Shotgun", Run(engine, session, "String(Weapon.Shotgun)").Value!.Get<string>());
		Assert.False(engine.Compile("Int32(\"12\")", session).IsValid);
		Assert.False(engine.Compile("Bool(1)", session).IsValid);
	}

	[Fact]
	public void ConversionPropagatesOperandErrorsAndCombinesErrorTypes()
	{
		var engine = new ShellEngine();
		var session = new ShellSession();
		var core = engine.Core;
		var original = engine.CreateValue(core.Error, new ShellError("original"));
		session.SetBinding("failed", engine.CreateError(core.Int64, core.Error, original));
		var compilation = Valid(engine, session, "Int32(failed)");
		Assert.Equal(engine.Catalog.ResultOf(core.Int32, core.Error), compilation.ResultType);
		var result = Run(engine, session, "Int32(failed)");
		Assert.Same(original, Assert.IsType<ShellResultValue.Error>(result.Value!.Value).Value);
	}

	[Fact]
	public void FixedProviderAndEnumValuesAreNormalImmutableExpressions()
	{
		var (engine, session) = Fixture();
		Assert.Equal(0F, Run(engine, session, "Vector3.zero.x").Value!.Get<float>());
		Assert.Equal(1F, Run(engine, session, "Vector3.up.y").Value!.Get<float>());
		Assert.Equal(1F, Run(engine, session, "Quaternion.identity.w").Value!.Get<float>());
		Assert.Equal(1F, Run(engine, session, "Color.white.a").Value!.Get<float>());
		Assert.Equal(-9.81F, Run(engine, session, "Physics.gravity.y").Value!.Get<float>());
		Assert.Equal(5, Run(engine, session, "Weapon.values -> count").Value!.Get<int>());
		Assert.Equal("Crowbar", Run(engine, session, "String(Weapon.values -> first -> require)").Value?.Get<string>());
		Run(engine, session, "weapons = Weapon.values");
		Assert.Equal(5, engine.GetArrayItems(session.Get("weapons")).Count);
	}

	[Fact]
	public void ProvidersRunPerReferenceAndHostFailuresAreContained()
	{
		var engine = new ShellEngine();
		var core = engine.Core;
		var calls = 0;
		var owner = TypeDescriptorBuilder.For<int>("Constants")
			.Value<int>("fixed", "Fixed value.", core.Int32, 3)
			.ProvidedValue<int>("next", "Changing value.", core.Int32, _ => ++calls)
			.Build();
		Assert.True(engine.Register(new DescriptorSet(types: [owner])).Success);
		var session = new ShellSession();
		Assert.Equal(3, Run(engine, session, "Constants.fixed").Value!.Get<int>());
		Assert.Equal(3, Run(engine, session, "[Constants.next, Constants.next] -> sum").Value!.Get<int>());
		Assert.Equal(2, calls);

		AssertHostFault(engine, session, new TypeValueDescriptor("null_value", "Null.", core.Int32, _ => null!), "SL5120");
		AssertHostFault(engine, session, new TypeValueDescriptor("wrong", "Wrong type.", core.Int32,
			context => context.Engine.CreateValue(core.String, "wrong")), "SL5120");
		var adapter = new ToggleIntAdapter();
		var payload = new TypeDescriptor("ProviderPayload", "Provider payload.", typeof(int), adapter);
		Assert.True(engine.Register(new DescriptorSet(types: [payload])).Success);
		var invalidValue = engine.CreateValue(payload.Id, 1);
		adapter.IsEnabled = false;
		AssertHostFault(engine, session, new TypeValueDescriptor("invalid", "Invalid CLR.", payload.Id,
			_ => invalidValue), "SL5121");
		AssertHostFault(engine, session, new TypeValueDescriptor("throws", "Throws.", core.Int32,
			_ => throw new InvalidOperationException("failed")), "SL5122");
		AssertHostFault(engine, session, new TypeValueDescriptor("mutates", "Mutates.", core.Int32, context =>
		{
			context.Session.SetBinding("changed", context.Engine.CreateValue(core.Int32, 1));
			return context.Engine.CreateValue(core.Int32, 1);
		}), "SL5122");
		Assert.False(session.TryGetBinding("changed", out _));
	}

	[Fact]
	public void RegistrationRejectsInvalidScopedValuesAtomically()
	{
		var engine = new ShellEngine();
		var core = engine.Core;
		var revision = engine.CatalogRevision;
		var duplicate = new TypeDescriptor("DuplicateValues", "Duplicate values.", typeof(int), new ValueAdapter<int>(),
			typeValues: [
				new TypeValueDescriptor("same", "First.", core.Int32, engine.CreateValue(core.Int32, 1)),
				new TypeValueDescriptor("same", "Second.", core.Int32, engine.CreateValue(core.Int32, 2))]);
		var rejected = engine.Register(new DescriptorSet(types: [duplicate]));
		Assert.Contains(rejected.Diagnostics, x => x.Code == "SL3024");
		Assert.Equal(revision, engine.CatalogRevision);

		var invalid = new TypeDescriptor("InvalidValue", "Invalid value.", typeof(int), new ValueAdapter<int>(),
			typeValues: [new TypeValueDescriptor("value", "Value.", core.Int32,
				engine.CreateValue(core.String, "wrong"))]);
		rejected = engine.Register(new DescriptorSet(types: [invalid]));
		Assert.Contains(rejected.Diagnostics, x => x.Code == "SL3024");
		Assert.Equal(revision, engine.CatalogRevision);

		var enumType = new EnumTypeDescriptor("ReservedEnum", "Reserved enum.", typeof(int), new ValueAdapter<int>(),
			[new EnumMemberDescriptor("values", 0)]);
		rejected = engine.Register(new DescriptorSet(enums: [enumType]));
		Assert.Contains(rejected.Diagnostics, x => x.Code == "SL3024");
		Assert.Equal(revision, engine.CatalogRevision);
	}

	[Fact]
	public void ConversionCallableNamesCannotBeRegisteredAsCommands()
	{
		var engine = new ShellEngine();
		var command = new CommandDescriptor("Float32", "Colliding command.", null, null,
			[new OutputPortDescriptor("value", "Value.", engine.Core.Int32)],
			(context, _) => CommandOutcome.Success.Single("value", context.Engine.CreateValue(engine.Core.Int32, 1)));
		var revision = engine.CatalogRevision;
		var rejected = engine.Register(new DescriptorSet(commands: [command]));
		Assert.Contains(rejected.Diagnostics, x => x.Code == "SL3023");
		Assert.Equal(revision, engine.CatalogRevision);
	}

	[Fact]
	public void TypeQualificationHelpAndCompletionUseTypePrecedence()
	{
		var (engine, session) = Fixture();
		session.SetBinding("Vector3", engine.CreateValue(engine.Core.Int32, 7));
		Assert.Equal(0F, Run(engine, session, "Vector3.zero.x").Value!.Get<float>());
		var vector = engine.Catalog.Types.Single(x => x.Name == "Vector3");
		var help = engine.GetTypeHelp(vector.Id)!;
		Assert.Contains(help.TypeValues, x => x.Name == "zero" && x.Type == vector.Id);
		Assert.NotEmpty(help.Arguments);
		var floatHelp = engine.GetTypeHelp(engine.Core.Float32)!;
		Assert.Contains(floatHelp.Conversions, x => x.SourceType == engine.Core.Int32 && x.IsFallible);
		Assert.Contains(engine.GetCompletions("Vector3.", 8, session).Items, x => x.InsertionText == "zero");
		Assert.Contains(engine.GetCompletions("Weapon.", 7, session).Items, x => x.InsertionText == "values");
		Assert.Contains(engine.GetCompletions("Flo", 3, session).Items, x => x.InsertionText == "Float32");
		Assert.DoesNotContain(engine.GetCompletions("1 -> Flo", 8, session).Items, x => x.InsertionText == "Float32");
		Assert.DoesNotContain(engine.GetCompletions("Vector3(1, 2, 3) -> Flo", 24, session).Items,
			x => x.InsertionText == "Float32");
	}

	private static (ShellEngine Engine, ShellSession Session) Fixture()
	{
		var engine = new ShellEngine();
		Assert.True(new MockGame().Register(engine).Success);
		return (engine, new ShellSession());
	}

	private static ShellCompilation Valid(ShellEngine engine, ShellSession session, string source)
	{
		var compilation = engine.Compile(source, session);
		Assert.True(compilation.IsValid, string.Join(Environment.NewLine, compilation.Diagnostics));
		return compilation;
	}

	private static ExecutionResult Run(ShellEngine engine, ShellSession session, string source)
	{
		var result = engine.Execute(Valid(engine, session, source), session);
		Assert.True(result.Status == ExecutionStatus.Completed,
			result.RuntimeFault is { } runtime ? $"{runtime.Code}: {runtime.Message}" : result.HostFault?.Message);
		return result;
	}

	private static void AssertError(ExecutionResult result) => Assert.IsType<ShellResultValue.Error>(result.Value!.Value);

	private static void AssertHostFault(ShellEngine engine, ShellSession session, TypeValueDescriptor value, string code)
	{
		var type = new TypeDescriptor($"Owner{code}{value.Name}", "Provider owner.", typeof(int), new ValueAdapter<int>(),
			typeValues: [value]);
		Assert.True(engine.Register(new DescriptorSet(types: [type])).Success);
		var result = engine.Execute(Valid(engine, session, $"{type.Name}.{value.Name}"), session);
		Assert.Equal(ExecutionStatus.HostFault, result.Status);
		Assert.Equal(code, result.HostFault!.Code);
	}
}

internal sealed class ToggleIntAdapter : ValueAdapter
{
	public bool IsEnabled { get; set; } = true;
	public override Type ClrType => typeof(int);
	public override bool IsValid(object value) => IsEnabled && value is int;
	public override object GetClrValue(ShellValue value) => value.Get<int>();
	public override ShellValue CreateShellValue(object value) => throw new NotSupportedException();
}

internal static class TypeExpressionSessionExtensions
{
	public static ShellValue Get(this ShellSession session, string name)
	{
		Assert.True(session.TryGetBinding(name, out var value));
		return value;
	}
}
