using System.Collections.ObjectModel;

namespace ShellLang;

public readonly record struct SymbolId(int Value)
{
    public override string ToString() => Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
}

public readonly record struct ShellTypeId(int Value)
{
    public override string ToString() => Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
}

public readonly record struct RuntimeFaultCode
{
    public RuntimeFaultCode(string value) => Value = value ?? throw new ArgumentNullException(nameof(value));
    public string Value { get; }
    public override string ToString() => Value;
}

public readonly record struct SourceSpan(int Offset, int Length, int Line = 1, int Column = 1)
{
    public int End => checked(Offset + Length);
    public static SourceSpan FromBounds(string source, int start, int end)
    {
        var line = 1;
        var column = 1;
        for (var i = 0; i < start && i < source.Length; i++)
        {
            if (source[i] == '\n') { line++; column = 1; }
            else { column++; }
        }
        return new SourceSpan(start, Math.Max(0, end - start), line, column);
    }
}

public enum DiagnosticSeverity { Error, Warning, Information }

public sealed class CompilationDiagnostic
{
    internal CompilationDiagnostic(string code, string message, SourceSpan source,
        ShellTypeId? expectedType = null, ShellTypeId? actualType = null,
        IReadOnlyList<string>? attemptedAdaptations = null, string? symbolName = null)
    {
        Code = code; Message = message; Source = source;
        ExpectedType = expectedType; ActualType = actualType;
        AttemptedAdaptations = attemptedAdaptations ?? Array.Empty<string>();
        SymbolName = symbolName;
    }
    public string Code { get; }
    public DiagnosticSeverity Severity => DiagnosticSeverity.Error;
    public string Message { get; }
    public SourceSpan Source { get; }
    public ShellTypeId? ExpectedType { get; }
    public ShellTypeId? ActualType { get; }
    public IReadOnlyList<string> AttemptedAdaptations { get; }
    public string? SymbolName { get; }
    public override string ToString() => $"{Code} ({Source.Line},{Source.Column}): {Message}";
}

public sealed class HostingDiagnostic
{
    public HostingDiagnostic(string code, string message) { Code = code; Message = message; }
    public string Code { get; }
    public string Message { get; }
    public override string ToString() => $"{Code}: {Message}";
}

public sealed class ShellValue
{
    internal ShellValue(ShellTypeId type, object value)
    {
        Type = type;
        Value = value ?? throw new ArgumentNullException(nameof(value));
    }
    public ShellTypeId Type { get; }
    public object Value { get; }
    public T Get<T>() => Value is T value
        ? value
        : throw new InvalidCastException($"Shell value {Type} does not contain {typeof(T).Name}.");
    public override string ToString() => Value.ToString() ?? string.Empty;
}

public abstract record ShellResultValue
{
    private ShellResultValue() { }
    public sealed record Success(ShellValue Value) : ShellResultValue;
    public sealed record VoidSuccess : ShellResultValue;
    public sealed record Error : ShellResultValue
    {
        public Error(ShellValue value, IReadOnlyList<ErrorContextFrame>? frames = null)
        { Value = value ?? throw new ArgumentNullException(nameof(value)); Frames = frames ?? Array.Empty<ErrorContextFrame>(); }
        public ShellValue Value { get; }
        public IReadOnlyList<ErrorContextFrame> Frames { get; }
    }
}

public sealed record ErrorContextFrame(
    string Kind,
    string Name,
    SourceSpan Source,
    int? ArrayIndex = null);

public sealed class RuntimeFault
{
    internal RuntimeFault(RuntimeFaultCode code, string message, SourceSpan source,
        IReadOnlyList<ErrorContextFrame>? context = null)
    {
        Code = code; Message = message; Source = source;
        Context = context ?? Array.Empty<ErrorContextFrame>();
    }
    public RuntimeFaultCode Code { get; }
    public string Message { get; }
    public SourceSpan Source { get; }
    public IReadOnlyList<ErrorContextFrame> Context { get; }
}

public sealed class HostFault
{
    internal HostFault(string code, string message, SourceSpan source, Exception? exception = null,
        IReadOnlyList<ErrorContextFrame>? context = null)
    {
        Code = code; Message = message; Source = source; Exception = exception;
        Context = context ?? Array.Empty<ErrorContextFrame>();
    }
    public string Code { get; }
    public string Message { get; }
    public SourceSpan Source { get; }
    public IReadOnlyList<ErrorContextFrame> Context { get; }
    public Exception? Exception { get; }
}

public enum ExecutionStatus { Completed, RuntimeFault, HostFault }

public sealed class ExecutionResult
{
    internal ExecutionResult(ExecutionStatus status, ShellValue? value, RuntimeFault? runtimeFault,
        HostFault? hostFault, int completedStatementCount)
    {
        Status = status; Value = value; RuntimeFault = runtimeFault; HostFault = hostFault;
        CompletedStatementCount = completedStatementCount;
    }
    public ExecutionStatus Status { get; }
    public ShellValue? Value { get; }
    public RuntimeFault? RuntimeFault { get; }
    public HostFault? HostFault { get; }
    public int CompletedStatementCount { get; }
}

public sealed record SessionRequirement(string Name, ShellTypeId Type);
public sealed record SessionBindingInfo(string Name, ShellTypeId Type);

public sealed class SessionUpdateResult
{
    internal SessionUpdateResult(bool added, bool typeChanged) { Added = added; TypeChanged = typeChanged; }
    public bool Added { get; }
    public bool TypeChanged { get; }
}

public sealed class ShellSession
{
    private readonly Dictionary<string, ShellValue> _bindings = new(StringComparer.Ordinal);
    internal bool IsExecuting { get; set; }
    public long SchemaRevision { get; private set; }
    public bool TryGetBinding(string name, out ShellValue value) => _bindings.TryGetValue(name, out value!);
    public SessionUpdateResult SetBinding(string name, ShellValue value)
    {
        ArgumentException.ThrowIfNullOrEmpty(name); ArgumentNullException.ThrowIfNull(value);
        var added = !_bindings.TryGetValue(name, out var old);
        var changed = !added && old!.Type != value.Type;
        _bindings[name] = value;
        if (added || changed) SchemaRevision++;
        return new SessionUpdateResult(added, changed);
    }
    public bool RemoveBinding(string name)
    {
        if (!_bindings.Remove(name)) return false;
        SchemaRevision++; return true;
    }
    public IReadOnlyList<SessionBindingInfo> GetBindings() => _bindings
        .OrderBy(static pair => pair.Key, StringComparer.Ordinal)
        .Select(static pair => new SessionBindingInfo(pair.Key, pair.Value.Type)).ToArray();
}

public sealed class RegistrationResult
{
    internal RegistrationResult(IReadOnlyList<HostingDiagnostic> diagnostics) => Diagnostics = diagnostics;
    public bool Success => Diagnostics.Count == 0;
    public IReadOnlyList<HostingDiagnostic> Diagnostics { get; }
}

public sealed class CompilationOptions;

public interface IExecutionObserver
{
    void StatementCompleted(int statementIndex, SourceSpan source, ShellValue? value);
}

public sealed class ExecutionOptions
{
    public IExecutionObserver? Observer { get; init; }
}

public enum CompletionItemKind { Binding, Global, Type, Command, Intrinsic, Member, Argument, Port, EnumMember }
public sealed record CompletionItem(SourceSpan ReplacementSpan, string InsertionText,
    CompletionItemKind Kind, string DisplayType, string Description);
public sealed class CompletionList
{
    internal CompletionList(IReadOnlyList<CompletionItem> items) => Items = items;
    public IReadOnlyList<CompletionItem> Items { get; }
}

public sealed record HelpParameter(string Name, ShellTypeId Type, string Description,
    bool Required = true, ShellValue? DefaultValue = null, bool IsDefault = false);

public sealed class HelpItem
{
    internal HelpItem(SymbolId id, string name, string kind, string description,
        IReadOnlyList<HelpParameter>? inputs = null, IReadOnlyList<HelpParameter>? arguments = null,
        IReadOnlyList<HelpParameter>? outputs = null, ShellTypeId? errorType = null,
        IReadOnlyList<RuntimeFaultDescriptor>? runtimeFaults = null, IReadOnlyList<string>? members = null)
    {
        Id = id; Name = name; Kind = kind; Description = description;
        Inputs = inputs ?? Array.Empty<HelpParameter>(); Arguments = arguments ?? Array.Empty<HelpParameter>();
        Outputs = outputs ?? Array.Empty<HelpParameter>(); ErrorType = errorType;
        RuntimeFaults = runtimeFaults ?? Array.Empty<RuntimeFaultDescriptor>();
        Members = members ?? Array.Empty<string>();
    }
    public SymbolId Id { get; }
    public string Name { get; }
    public string Kind { get; }
    public string Description { get; }
    public IReadOnlyList<HelpParameter> Inputs { get; }
    public IReadOnlyList<HelpParameter> Arguments { get; }
    public IReadOnlyList<HelpParameter> Outputs { get; }
    public ShellTypeId? ErrorType { get; }
    public IReadOnlyList<RuntimeFaultDescriptor> RuntimeFaults { get; }
    public IReadOnlyList<string> Members { get; }
}

public sealed class IntrinsicDescriptor
{
    internal IntrinsicDescriptor(SymbolId id, string name, string description) { Id = id; Name = name; Description = description; }
    public SymbolId Id { get; }
    public string Name { get; }
    public string Description { get; }
}

internal sealed class ShellArrayValue
{
    public ShellArrayValue(IEnumerable<ShellValue> items) => Items = Array.AsReadOnly(items.ToArray());
    public ReadOnlyCollection<ShellValue> Items { get; }
    public override string ToString() => $"[{string.Join(", ", Items)}]";
}

internal sealed class ShellOutputRecordValue
{
    public ShellOutputRecordValue(IReadOnlyDictionary<string, ShellValue> fields) =>
        Fields = new ReadOnlyDictionary<string, ShellValue>(new Dictionary<string, ShellValue>(fields, StringComparer.Ordinal));
    public IReadOnlyDictionary<string, ShellValue> Fields { get; }
    public override string ToString() => $"{{{string.Join(", ", Fields.Select(static p => $"{p.Key}: {p.Value}"))}}}";
}
