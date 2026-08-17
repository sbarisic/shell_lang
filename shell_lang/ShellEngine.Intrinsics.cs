using System.Collections.ObjectModel;

namespace ShellLang;

internal delegate EvalOutcome IntrinsicEvaluatorHandler(Evaluator evaluator, BoundIntrinsicOperation operation,
	ShellValue primary, IReadOnlyDictionary<string, ShellValue> arguments, IReadOnlyList<int> path);
internal enum IntrinsicBindingStrategy { Result, ContextualElement, ValueArguments, Collection }

internal sealed record IntrinsicSchema(
	IntrinsicKind Kind,
	string Name,
	string Description,
	IntrinsicPrimaryShape PrimaryShape,
	IReadOnlyList<IntrinsicSignatureDescriptor> Signatures,
	IntrinsicBindingStrategy BindingStrategy,
	IntrinsicEvaluatorHandler Evaluator);

public sealed partial class ShellEngine
{
	private static IntrinsicSignatureDescriptor Signature(string primary, string result,
		IntrinsicConstraintKind constraint = IntrinsicConstraintKind.None,
		params IntrinsicParameterDescriptor[] parameters) => new(primary, result, parameters, constraint);
	private static IntrinsicParameterDescriptor Value(string name, string type) => new(name, type, IntrinsicParameterRole.Value);
	private static IntrinsicParameterDescriptor Context(string name, string type) => new(name, type, IntrinsicParameterRole.ContextualExpression);

	internal static readonly IReadOnlyDictionary<string, IntrinsicSchema> IntrinsicSchemas =
		new ReadOnlyDictionary<string, IntrinsicSchema>(new[]
		{
			S(IntrinsicKind.Require, "require", "Return an Ok value or fault on Err.", IntrinsicPrimaryShape.Result, Signature("Result<T,E>", "T")),
			S(IntrinsicKind.ValueOr, "value_or", "Return an Ok value or a supplied default.", IntrinsicPrimaryShape.Result,
				Signature("Result<T,E>", "T", parameters: [Value("default", "T")])),
			S(IntrinsicKind.Error, "error", "Return the error from an Err value.", IntrinsicPrimaryShape.Result, Signature("Result<T,E>", "E")),
			S(IntrinsicKind.IsOk, "is_ok", "Report whether a Result is Ok.", IntrinsicPrimaryShape.Result, Signature("Result<T,E>", "Bool")),
			S(IntrinsicKind.Where, "where", "Keep elements that satisfy a contextual predicate.", IntrinsicPrimaryShape.Array,
				Signature("Array<T>", "Array<T>", parameters: [Context("predicate", "T -> Bool")])),
			S(IntrinsicKind.Sort, "sort", "Stable-sort elements by a contextual key.", IntrinsicPrimaryShape.Array,
				Signature("Array<T>", "Array<T>", IntrinsicConstraintKind.Ordering, Context("by", "T -> K"))),
			S(IntrinsicKind.Take, "take", "Return up to the first count elements.", IntrinsicPrimaryShape.Array,
				Signature("Array<T>", "Array<T>", parameters: [Value("count", "Int32")])),
			S(IntrinsicKind.Count, "count", "Return the number of elements.", IntrinsicPrimaryShape.Array, Signature("Array<T>", "Int32")),
			S(IntrinsicKind.Sum, "sum", "Add all numeric elements.", IntrinsicPrimaryShape.Array,
				Signature("Array<T>", "T", IntrinsicConstraintKind.Numeric)),
			S(IntrinsicKind.First, "first", "Return the first element or EmptyCollectionError.", IntrinsicPrimaryShape.Array,
				Signature("Array<T>", "Result<T,EmptyCollectionError>")),
			S(IntrinsicKind.Min, "min", "Return the least element or EmptyCollectionError.", IntrinsicPrimaryShape.Array,
				Signature("Array<T>", "Result<T,EmptyCollectionError>", IntrinsicConstraintKind.Ordering)),
			S(IntrinsicKind.Max, "max", "Return the greatest element or EmptyCollectionError.", IntrinsicPrimaryShape.Array,
				Signature("Array<T>", "Result<T,EmptyCollectionError>", IntrinsicConstraintKind.Ordering)),
			S(IntrinsicKind.Average, "average", "Return the numeric average or EmptyCollectionError.", IntrinsicPrimaryShape.Array,
				Signature("Array<T>", "Result<Average<T>,EmptyCollectionError>", IntrinsicConstraintKind.Numeric)),
			S(IntrinsicKind.At, "at", "Return the element at a positive or end-relative index.", IntrinsicPrimaryShape.Array,
				Signature("Array<T>", "T", parameters: [Value("index", "Int32")])),
			S(IntrinsicKind.Last, "last", "Return the last element or EmptyCollectionError.", IntrinsicPrimaryShape.Array,
				Signature("Array<T>", "Result<T,EmptyCollectionError>")),
			S(IntrinsicKind.Skip, "skip", "Return the elements after an initial count.", IntrinsicPrimaryShape.Array,
				Signature("Array<T>", "Array<T>", parameters: [Value("count", "Int32")])),
			S(IntrinsicKind.Slice, "slice", "Return a strict contiguous array range.", IntrinsicPrimaryShape.Array,
				Signature("Array<T>", "Array<T>", parameters: [Value("start", "Int32"), Value("count", "Int32")])),
			S(IntrinsicKind.Any, "any", "Report whether any element satisfies a contextual predicate.", IntrinsicPrimaryShape.Array,
				Signature("Array<T>", "Bool", parameters: [Context("predicate", "T -> Bool")])),
			S(IntrinsicKind.All, "all", "Report whether every element satisfies a contextual predicate.", IntrinsicPrimaryShape.Array,
				Signature("Array<T>", "Bool", parameters: [Context("predicate", "T -> Bool")])),
			S(IntrinsicKind.Select, "select", "Transform each element with a contextual selector.", IntrinsicPrimaryShape.Array,
				Signature("Array<T>", "Array<K>", parameters: [Context("selector", "T -> K")])),
			S(IntrinsicKind.Contains, "contains", "Report whether an equal element is present.", IntrinsicPrimaryShape.Array,
				Signature("Array<T>", "Bool", IntrinsicConstraintKind.Equality, Value("value", "T"))),
			S(IntrinsicKind.Concat, "concat", "Append another assignable array.", IntrinsicPrimaryShape.Array,
				Signature("Array<T>", "Array<T>", parameters: [Value("other", "Array<T>")])),
			S(IntrinsicKind.Distinct, "distinct", "Keep the first element for each equal value or key.", IntrinsicPrimaryShape.Array,
				Signature("Array<T>", "Array<T>", IntrinsicConstraintKind.Equality),
				Signature("Array<T>", "Array<T>", IntrinsicConstraintKind.Equality, Context("by", "T -> K"))),
			S(IntrinsicKind.Reverse, "reverse", "Return the elements in reverse order.", IntrinsicPrimaryShape.Array,
				Signature("Array<T>", "Array<T>")),
			S(IntrinsicKind.Single, "single", "Return the only element or CollectionCardinalityError.", IntrinsicPrimaryShape.Array,
				Signature("Array<T>", "Result<T,CollectionCardinalityError>")),
			S(IntrinsicKind.Flatten, "flatten", "Remove exactly one nested array layer.", IntrinsicPrimaryShape.NestedArray,
				Signature("Array<Array<T>>", "Array<T>"))
		}.ToDictionary(x => x.Name, StringComparer.Ordinal));

	internal static readonly HashSet<string> IntrinsicNames = new(IntrinsicSchemas.Keys, StringComparer.Ordinal);
	internal static readonly IReadOnlyDictionary<IntrinsicKind, IntrinsicSchema> IntrinsicSchemasByKind =
		new ReadOnlyDictionary<IntrinsicKind, IntrinsicSchema>(IntrinsicSchemas.Values.ToDictionary(x => x.Kind));

	private static IntrinsicSchema S(IntrinsicKind kind, string name, string description,
		IntrinsicPrimaryShape primary, params IntrinsicSignatureDescriptor[] signatures) =>
		new(kind, name, description, primary, Array.AsReadOnly(signatures), BindingStrategy(kind),
			ShellLang.Evaluator.CreateIntrinsicHandler(kind));

	private static IntrinsicBindingStrategy BindingStrategy(IntrinsicKind kind) => kind switch
	{
		IntrinsicKind.Require or IntrinsicKind.ValueOr or IntrinsicKind.Error or IntrinsicKind.IsOk =>
			IntrinsicBindingStrategy.Result,
		IntrinsicKind.Where or IntrinsicKind.Sort or IntrinsicKind.Any or IntrinsicKind.All or IntrinsicKind.Select or
			IntrinsicKind.Distinct => IntrinsicBindingStrategy.ContextualElement,
		IntrinsicKind.Take or IntrinsicKind.Skip or IntrinsicKind.At or IntrinsicKind.Slice or IntrinsicKind.Contains or
			IntrinsicKind.Concat => IntrinsicBindingStrategy.ValueArguments,
		_ => IntrinsicBindingStrategy.Collection
	};

	internal bool IntrinsicApplies(IntrinsicSchema schema, ShellTypeId type)
	{
		var entry = GetTypeEntry(type);
		if (entry.Kind == ShellTypeKind.Result)
			return schema.PrimaryShape == IntrinsicPrimaryShape.Result || IntrinsicApplies(schema, entry.SuccessType!.Value);
		if (entry.Kind == ShellTypeKind.OutputRecord && entry.DefaultOutput is { } field)
			return IntrinsicApplies(schema, entry.OutputFields![field]);
		if (entry.Kind != ShellTypeKind.Array || schema.PrimaryShape == IntrinsicPrimaryShape.Result)
			return false;
		var element = GetTypeEntry(entry.ElementType!.Value);
		if (schema.PrimaryShape == IntrinsicPrimaryShape.NestedArray)
			return element.Kind == ShellTypeKind.Array;
		return schema.Kind switch
		{
			IntrinsicKind.Sum or IntrinsicKind.Average => IsNumericType(element.Id),
			IntrinsicKind.Min or IntrinsicKind.Max => element.Ordering is not null,
			IntrinsicKind.Contains => element.Equality is not null,
			_ => true
		};
	}
}
