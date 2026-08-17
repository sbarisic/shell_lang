namespace ShellLang;

public sealed record ConversionError(ShellTypeId SourceType, ShellTypeId TargetType, string Reason);
