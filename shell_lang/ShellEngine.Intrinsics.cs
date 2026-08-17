using System.Collections.ObjectModel;

namespace ShellLang;

public sealed partial class ShellEngine
{
	internal static readonly IReadOnlyDictionary<string, string> IntrinsicDescriptions =
		new ReadOnlyDictionary<string, string>(new Dictionary<string, string>(StringComparer.Ordinal)
		{
			["require"] = "Return an Ok value or fault on Err.",
			["value_or"] = "Return an Ok value or a supplied default.",
			["error"] = "Return the error from an Err value.",
			["is_ok"] = "Report whether a Result is Ok.",
			["where"] = "Keep elements that satisfy a contextual predicate.",
			["sort"] = "Stable-sort elements by a contextual key.",
			["take"] = "Return up to the first count elements.",
			["count"] = "Return the number of elements.",
			["sum"] = "Add all numeric elements.",
			["first"] = "Return the first element or EmptyCollectionError.",
			["min"] = "Return the least element or EmptyCollectionError.",
			["max"] = "Return the greatest element or EmptyCollectionError.",
			["average"] = "Return the numeric average or EmptyCollectionError.",
			["at"] = "Return the element at a positive or end-relative index.",
			["last"] = "Return the last element or EmptyCollectionError.",
			["skip"] = "Return the elements after an initial count.",
			["slice"] = "Return a strict contiguous array range.",
			["any"] = "Report whether any element satisfies a contextual predicate.",
			["all"] = "Report whether every element satisfies a contextual predicate.",
			["select"] = "Transform each element with a contextual selector.",
			["contains"] = "Report whether an equal element is present.",
			["concat"] = "Append another assignable array.",
			["distinct"] = "Keep the first element for each equal value or key.",
			["reverse"] = "Return the elements in reverse order.",
			["single"] = "Return the only element or CollectionCardinalityError."
		});

	internal static readonly HashSet<string> IntrinsicNames = new(IntrinsicDescriptions.Keys, StringComparer.Ordinal);
}
