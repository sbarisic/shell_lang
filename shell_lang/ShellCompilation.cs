namespace ShellLang;

public sealed class ShellCompilation
{
	internal ShellCompilation(ShellEngine engine, string source, IReadOnlyList<CompilationDiagnostic> diagnostics,
		ShellTypeId? resultType, long catalogRevision, IReadOnlyList<SessionRequirement> requirements, BoundProgram? program)
	{
		Engine = engine;
		Source = source;
		Diagnostics = diagnostics;
		ResultType = resultType;
		CatalogRevision = catalogRevision;
		SessionRequirements = requirements;
		Program = program;
	}
	internal ShellEngine Engine
	{
		get;
	}
	internal BoundProgram? Program
	{
		get;
	}
	public string Source
	{
		get;
	}
	public bool IsValid => Diagnostics.Count == 0 && Program is not null;
	public IReadOnlyList<CompilationDiagnostic> Diagnostics
	{
		get;
	}
	public ShellTypeId? ResultType
	{
		get;
	}
	public long CatalogRevision
	{
		get;
	}
	public IReadOnlyList<SessionRequirement> SessionRequirements
	{
		get;
	}
}
