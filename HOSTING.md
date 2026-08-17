# ShellLang 0.1 C# Hosting Contract

## 1. Status

This document defines the C# host contract for ShellLang 0.1.

`LANGUAGE.md` defines source syntax and language semantics. This document defines how a .NET host exposes types, values, and operations.

The terms **MUST**, **MUST NOT**, **SHOULD**, and **MAY** define conformance requirements.

The API declarations in this document define the required public concepts. The implementation can add convenience overloads without changing their behavior.

## 2. Design rules

The host controls the complete ShellLang environment.

ShellLang MUST NOT discover CLR members through reflection. Public CLR members do not become script members automatically.

The host MUST register each accessible type, global, member, query, and command with an explicit descriptor.

The runtime invokes all operations synchronously on the caller's thread. ShellLang 0.1 has no scheduler and no implicit concurrency.

Registered member reads and queries MUST be read-only. Commands are the only registered operations that can change host state.

The host remains responsible for the effects and security of its command code.

## 3. Public model

The public API uses the `ShellLang` namespace.

The main public concepts are:

```csharp
namespace ShellLang;

public sealed class ShellEngine;
public sealed class ShellSession;
public sealed class ShellCompilation;
public sealed class ShellValue;
public abstract record ShellResultValue;
public readonly record struct RuntimeFaultCode;
public sealed record EmptyCollectionError;
public sealed record CollectionCardinalityError;

public sealed class DescriptorSet;
public sealed class TypeDescriptor;
public sealed class EnumTypeDescriptor;
public sealed class ErrorTypeDescriptor;
public sealed class GlobalDescriptor;
public sealed class MemberDescriptor;
public sealed class QueryDescriptor;
public sealed class CommandDescriptor;
public sealed class InputPortDescriptor;
public sealed class ArgumentDescriptor;
public sealed class OutputPortDescriptor;
public sealed class RuntimeFaultDescriptor;

public sealed class CompilationDiagnostic;
public sealed class RuntimeFault;
public sealed class HostFault;
public sealed class ExecutionResult;
public sealed class CompletionList;
public sealed class HelpItem;
```

Descriptors MUST be immutable after construction. A builder MAY provide mutable construction state before it creates a descriptor.

## 4. ShellEngine

`ShellEngine` owns one descriptor catalog and its compiler services.

Its semantic API is:

```csharp
public sealed class ShellEngine
{
    public DescriptorCatalog Catalog { get; }
    public long CatalogRevision { get; }

    public RegistrationResult Register(DescriptorSet descriptors);

    public ShellCompilation Compile(
        string source,
        ShellSession session,
        CompilationOptions? options = null);

    public ExecutionResult Execute(
        ShellCompilation compilation,
        ShellSession session,
        ExecutionOptions? options = null);

    public CompletionList GetCompletions(
        string source,
        int position,
        ShellSession session);

    public HelpItem? GetHelp(SymbolId symbol);
}
```

`Register` MUST validate the full descriptor set before it changes the catalog. Registration is atomic.

A successful registration increments `CatalogRevision` once. A failed registration does not change the catalog or its revision.

The engine MUST include core types and compiler intrinsics in every catalog. A host cannot replace them.

`CoreTypeCatalog` exposes `EmptyCollectionError` and `CollectionCardinalityError` type identifiers. A `CollectionCardinalityError` value contains the actual array count that caused `single` to fail.

An engine instance MAY compile scripts for several sessions. Descriptors are shared across those sessions.

A `DescriptorSet` can contain types, globals, commands, and runtime fault descriptors. The engine validates their references as one atomic set.

## 5. Descriptor catalog

### 5.1 Symbol identity

Each registered symbol has a stable `SymbolId` within one engine.

A type also has a `ShellTypeId`. Constructed core types such as `Array<Player>` and `Result<Player,GameError>` use interned type identifiers.

String names are display and source names. Runtime type equality uses `ShellTypeId`, not CLR `Type` equality or text comparison.

### 5.2 Namespaces

The catalog maintains these name groups:

- Types
- Globals
- Commands and intrinsics
- Runtime fault codes
- Members within each receiver type
- Enum members within each enum type

Names MUST be unique within their group.

A command cannot use an intrinsic name. A property member and a query member cannot share a name on the same receiver type.

A global and a command MAY share a name because their source positions are distinct.

A session binding MAY shadow a global. It cannot replace a catalog entry.

### 5.3 Catalog stability

Each compilation records its engine's catalog revision.

`Execute` MUST compare the recorded revision with the current revision before it executes the first statement.

A mismatch produces a stale-compilation host fault. The runtime MUST NOT execute any statement.

## 6. Type descriptors

### 6.1 TypeDescriptor

A `TypeDescriptor` defines one nominal ShellLang type.

It contains at least:

```csharp
public sealed class TypeDescriptor
{
    public ShellTypeId Id { get; }
    public string Name { get; }
    public string Description { get; }
    public Type ClrType { get; }
    public IReadOnlyList<ShellTypeId> DirectBases { get; }
    public ValueAdapter Adapter { get; }
    public IReadOnlyList<MemberDescriptor> Members { get; }
    public IReadOnlyList<QueryDescriptor> Queries { get; }
    public EqualityDescriptor? Equality { get; }
    public OrderingDescriptor? Ordering { get; }
}
```

`Name` MUST be a valid ShellLang identifier and MUST be unique among types.

`DirectBases` defines nominal assignment. The runtime does not infer this list from `ClrType`.

The non-error type graph MUST be acyclic. It can contain several declared bases or interfaces.

A referenced base MUST already exist or appear in the same atomic descriptor set. The engine validates bases before derived types.

### 6.2 ValueAdapter

A `ValueAdapter` validates the boundary between a CLR value and a ShellLang type.

Its semantic API is:

```csharp
public abstract class ValueAdapter
{
    public abstract bool IsValid(object value);
    public abstract object GetClrValue(ShellValue value);
    public abstract ShellValue CreateShellValue(object value);
}
```

An adapter MUST reject `null`. It MUST reject a CLR value that does not satisfy its descriptor.

The engine MUST catch an exception from an adapter. It converts that exception to a host fault.

The adapter cannot change a value's declared `ShellTypeId` during execution.

### 6.3 Core generic types

The engine owns the `Array<T>` and `Result<T,E>` type constructors. A host registers only the element and error types.

The engine represents `Array<T>` as an immutable finite sequence of `ShellValue` items. It MUST NOT expose a mutable CLR array to script operations.

The engine represents `Result<T,E>` with one non-null `ShellResultValue` variant:

```csharp
public abstract record ShellResultValue
{
    public sealed record Success(ShellValue Value) : ShellResultValue;
    public sealed record VoidSuccess : ShellResultValue;
    public sealed record Error(ShellValue Value) : ShellResultValue;
}
```

`Success.Value` MUST be assignable to `T` when `T` is a value type. `VoidSuccess` is valid only when `T` is `Void`.

`Error.Value` MUST be assignable to `E`.

The outer `ShellValue.Value` stores the non-null variant object. `VoidSuccess` does not contain a `ShellValue` payload.

A host cannot register a new generic type constructor in version 0.1.

The engine MUST reject `Stream<T>` in all host descriptors.

### 6.4 EnumTypeDescriptor

An `EnumTypeDescriptor` defines a finite set of named values.

It contains:

- One nominal type identity
- One CLR enum or value adapter
- A unique ShellLang name for each member
- One CLR value for each member
- Optional ordering metadata

The engine uses these member names for contextual enum lookup and completion.

The host MUST register duplicate CLR aliases as separate ShellLang names only when it intends to expose both names.

### 6.5 ErrorTypeDescriptor

An `ErrorTypeDescriptor` defines one declared error type.

Every error descriptor except core `Error` MUST name exactly one direct error base. That base MUST be another error descriptor.

The engine MUST reject cycles, multiple error bases, and error chains that do not end at core `Error`.

This rule makes the nearest common error base deterministic.

### 6.6 Equality and ordering

Host values do not get equality or ordering from CLR methods automatically.

An `EqualityDescriptor` provides a synchronous equality delegate for one registered type.

An `OrderingDescriptor` provides a synchronous total-order delegate for one registered type.

These delegates MUST be read-only. The engine converts their exceptions to host faults.

## 7. ShellValue

`ShellValue` pairs one non-null CLR representation with one `ShellTypeId`.

Its semantic shape is:

```csharp
public sealed class ShellValue
{
    public ShellTypeId Type { get; }
    public object Value { get; }
}
```

Hosts MUST create values through the engine or the registered `ValueAdapter`. Public constructors SHOULD NOT permit unchecked values.

`ShellValue.Value` MUST NOT be `null`.

The runtime validates every value that crosses a descriptor boundary. This includes globals, members, query results, arguments, command inputs, command outputs, and declared errors.

## 8. Globals

A `GlobalDescriptor` exposes one named read-only value.

```csharp
public sealed class GlobalDescriptor
{
    public SymbolId Id { get; }
    public string Name { get; }
    public string Description { get; }
    public ShellTypeId Type { get; }
    public GlobalValueProvider GetValue { get; }
}
```

The engine calls `GetValue` each time an expression evaluates the global. It does not cache the value.

The provider MUST return the declared type. A null, wrong type, or exception produces a host fault.

A global is read-only from ShellLang. An assignment with the same name creates or replaces a session binding that shadows it.

## 9. Members and queries

### 9.1 MemberDescriptor

A `MemberDescriptor` exposes one read-only value on one receiver type.

```csharp
public sealed class MemberDescriptor
{
    public SymbolId Id { get; }
    public string Name { get; }
    public string Description { get; }
    public ShellTypeId ReceiverType { get; }
    public ShellTypeId ValueType { get; }
    public MemberGetter GetValue { get; }
}
```

The getter receives a validated receiver. It MUST return the declared type.

ShellLang 0.1 has no setter descriptor.

### 9.2 QueryDescriptor

A `QueryDescriptor` exposes one read-only operation on a receiver.

```csharp
public sealed class QueryDescriptor
{
    public SymbolId Id { get; }
    public string Name { get; }
    public string Description { get; }
    public ShellTypeId ReceiverType { get; }
    public IReadOnlyList<ArgumentDescriptor> Arguments { get; }
    public ShellTypeId OutputType { get; }
    public ShellTypeId? ErrorType { get; }
    public QueryInvoker Invoke { get; }
}
```

Query names MUST be unique on their receiver type. ShellLang 0.1 does not support query overloads.

A query can return a declared typed error. It MUST NOT change host state.

The engine cannot prove that a delegate is read-only. The host violates this contract when a member or query changes observable host state.

## 10. Command descriptors

### 10.1 CommandDescriptor

A `CommandDescriptor` defines one command.

```csharp
public sealed class CommandDescriptor
{
    public SymbolId Id { get; }
    public string Name { get; }
    public string Description { get; }
    public IReadOnlyList<InputPortDescriptor> Inputs { get; }
    public IReadOnlyList<ArgumentDescriptor> Arguments { get; }
    public IReadOnlyList<OutputPortDescriptor> Outputs { get; }
    public ShellTypeId? ErrorType { get; }
    public IReadOnlyList<RuntimeFaultCode> RuntimeFaults { get; }
    public CommandInvoker Invoke { get; }
}
```

Command names MUST be unique. Host commands are monomorphic in version 0.1.

Generic compiler intrinsics use internal type schemas. A host cannot register a generic command descriptor.

### 10.2 InputPortDescriptor

An input port contains:

- A unique name within the command
- A description
- A registered type
- An `IsDefault` flag

All input ports are required in version 0.1.

A command can have zero or one default input. The engine MUST reject more than one.

### 10.3 ArgumentDescriptor

An argument contains:

- A unique name within the command or query
- A description
- A registered type
- A stable positional index
- A required flag
- An optional constant default value

A required argument cannot have a default. An optional argument MUST have a valid default of its exact declared type.

Argument defaults are values. They cannot invoke code when the runtime uses them.

An argument name MUST NOT duplicate an input port name in the same command.

### 10.4 OutputPortDescriptor

An output port contains:

- A unique name within the command
- A description
- A registered type
- An `IsDefault` flag

A command can have zero or one default output. The engine MUST reject more than one.

When a command has several outputs, the engine generates one nominal output record type. The record contains all output fields.

The generated record belongs to that command. It cannot be structurally compatible with another command's record.

### 10.5 Declared error

`ErrorType` is optional. When present, it MUST reference a registered error descriptor.

An invoker can return a declared error type or any registered subtype of it.

An invoker without `ErrorType` MUST NOT return a declared error outcome.

### 10.6 Declared runtime faults

A `RuntimeFaultDescriptor` defines one deliberate host-command fault:

```csharp
public readonly record struct RuntimeFaultCode(string Value);

public sealed class RuntimeFaultDescriptor
{
    public RuntimeFaultCode Code { get; }
    public string Name { get; }
    public string Description { get; }
}
```

A host code has a prefix and four decimal digits. The prefix contains 2 to 16 uppercase ASCII letters, digits, or `_` characters.

The first prefix character MUST be a letter. A host prefix MUST NOT be `SL`.

`Name` MUST be a valid ShellLang identifier. `GAME1001` is a valid host code.

Core language runtime faults keep the `SL4xxx` range.

Runtime fault codes and names MUST be unique in one catalog. A command lists each runtime fault code that it can return.

A runtime fault is not a declared error type. A script cannot store, propagate, or recover from it.

## 11. Command invocation boundary

### 11.1 InvocationContext

The runtime creates one `InvocationContext` for each command or query call.

```csharp
public sealed class InvocationContext
{
    public ShellEngine Engine { get; }
    public ShellSession Session { get; }
    public IServiceProvider Services { get; }
    public SourceSpan Source { get; }
    public IReadOnlyList<int> ArrayIndexPath { get; }
}
```

`Services` gives trusted command code access to host services. ShellLang source cannot enumerate or access it directly.

`ArrayIndexPath` identifies the current lifted element. It is empty for a direct invocation.

### 11.2 Invocation values

The invoker receives validated input and argument maps.

```csharp
public sealed class InvocationValues
{
    public ShellValue GetInput(string name);
    public ShellValue GetArgument(string name);
}
```

Every required input and argument is present. The runtime applies argument defaults before it calls the invoker.

An invoker cannot change the map or replace its values.

### 11.3 Command outcome

A command returns one `CommandOutcome`.

```csharp
public abstract record CommandOutcome
{
    public sealed record Success(
        IReadOnlyDictionary<string, ShellValue> Outputs) : CommandOutcome;

    public sealed record Error(ShellValue Value) : CommandOutcome;

    public sealed record Fault(
        RuntimeFaultCode Code,
        string Message) : CommandOutcome;
}
```

A successful outcome MUST contain every declared output exactly once. It MUST NOT contain undeclared outputs.

A zero-output success contains an empty output map.

For a fallible zero-output command, the runtime converts that empty success to `ShellResultValue.VoidSuccess`.

For a non-fallible zero-output command, the runtime produces terminal `Void` without a `ShellValue`.

An error outcome MUST contain a value assignable to the command's declared error type.

A fault outcome MUST use a runtime fault code listed by the command descriptor. Its message MUST be non-empty and safe for scripts.

The engine validates an outcome before the script can use it.

An undeclared fault outcome is a host fault. It is not a command runtime fault.

### 11.4 Fault containment

The engine MUST catch exceptions from globals, adapters, members, queries, equality delegates, ordering delegates, commands, and execution observers.

It converts these exceptions to `HostFault` values. It MUST preserve the original exception for trusted host diagnostics.

The engine converts a valid `CommandOutcome.Fault` to `RuntimeFault`. It adds the invocation source and array-index path.

ShellLang source MUST receive a safe host-selected message. The runtime MUST NOT expose stack traces or private CLR values by default.

## 12. Explicit descriptor construction

Builders MAY use generic C# methods to reduce adapter code. They MUST still require explicit ShellLang names and metadata.

This example shows the intended API shape:

```csharp
var playerType = TypeDescriptorBuilder.For<Player>("Player")
    .Description("A player entity.")
    .Base(entityType.Id)
    .Member(
        name: "health",
        type: core.Int32,
        getter: player => player.Health)
    .Query(
        name: "distance",
        arguments: [Argument.Required<Entity>("to", entityType.Id)],
        output: core.Float32,
        invoke: (player, values) => player.DistanceTo(values.Get<Entity>("to")))
    .Build();

var damageCommand = CommandDescriptorBuilder.Create("damage")
    .Description("Damage one player.")
    .Input<Player>("target", playerType.Id, isDefault: true)
    .Argument<int>("amount", core.Int32)
    .Argument<DamageType>(
        "type",
        damageType.Id,
        defaultValue: DamageType.Normal)
    .Output<DamageResult>("result", damageResultType.Id, isDefault: true)
    .Error(damageErrorType.Id)
    .Invoke((context, values) =>
    {
        var target = values.GetInput<Player>("target");
        var amount = values.GetArgument<int>("amount");
        var type = values.GetArgument<DamageType>("type");

        return Damage(target, amount, type);
    })
    .Build();

var registration = engine.Register(new DescriptorSet(
    types: [playerType, damageType, damageResultType, damageErrorType],
    commands: [damageCommand]));
```

The builder gets data only from the supplied delegates and metadata. It MUST NOT expose other CLR members.

The example is not permission to infer names, descriptions, ports, defaults, or members through reflection.

## 13. Descriptor validation

`Register` MUST validate these rules before it changes the catalog:

1. All names follow the ShellLang identifier rules.
2. All names are unique in their catalog group.
3. No command uses an intrinsic name.
4. Every referenced type is a core type, an existing type, or a type in the same valid descriptor set.
5. The nominal type graph is acyclic.
6. Each error has one valid error base chain.
7. No descriptor uses `Stream<T>`.
8. Every descriptor has a non-empty description.
9. Every CLR adapter is present and compatible with its declared CLR type.
10. Each command has at most one default input and one default output.
11. Port, argument, output, member, and enum member names are unique in their local scope.
12. Every optional argument has one exact typed default.
13. Every command and query has one synchronous invoker.
14. Every member has one synchronous getter.
15. No host descriptor declares generic command type parameters.
16. Every runtime fault code and name is valid and unique.
17. Every command runtime fault reference resolves to a registered fault descriptor.

A failed registration returns one or more `HostingDiagnostic` items. It MUST report all independent validation errors that it can find safely.

The result has this semantic shape:

```csharp
public sealed class RegistrationResult
{
    public bool Success { get; }
    public IReadOnlyList<HostingDiagnostic> Diagnostics { get; }
}
```

## 14. ShellSession

`ShellSession` owns script-created bindings. It does not own globals or descriptors.

```csharp
public sealed class ShellSession
{
    public long SchemaRevision { get; }

    public bool TryGetBinding(string name, out ShellValue value);
    public SessionUpdateResult SetBinding(string name, ShellValue value);
    public bool RemoveBinding(string name);
    public IReadOnlyList<SessionBindingInfo> GetBindings();
}
```

Adding or removing a binding increments `SchemaRevision`. Replacing a binding with a different type also increments it.

Replacing a value with the same type does not change the schema revision.

The compiler records the exact external names and types that a compilation reads. The runtime validates those requirements before execution.

The session is not thread-safe in version 0.1. A host MUST NOT mutate or execute the same session concurrently.

The engine MUST reject recursive execution against a session that is already executing.

## 15. Compilation

### 15.1 ShellCompilation

`Compile` returns a `ShellCompilation` for valid and invalid source.

```csharp
public sealed class ShellCompilation
{
    public string Source { get; }
    public bool IsValid { get; }
    public IReadOnlyList<CompilationDiagnostic> Diagnostics { get; }
    public ShellTypeId? ResultType { get; }
    public long CatalogRevision { get; }
    public IReadOnlyList<SessionRequirement> SessionRequirements { get; }
}
```

An invalid compilation has no executable program. `Execute` MUST reject it without running source code.

The compiler processes statements in order. It updates its local symbol table after each statically valid assignment.

### 15.2 Source positions

`SourceSpan` uses a zero-based UTF-16 offset and length. It also exposes one-based line and column values for display.

All compiler diagnostics and runtime faults MUST identify a source span when source caused the error.

### 15.3 Diagnostic groups

Stable diagnostic codes use these groups:

| Range | Meaning |
| --- | --- |
| `SL1xxx` | Lexing and parsing |
| `SL2xxx` | Names, types, and connection adaptation |
| `SL3xxx` | Descriptor and host registration |
| `SL4xxx` | Runtime faults |
| `SL5xxx` | Host faults |

Removing or changing the meaning of a published code requires a later language version.

Host runtime fault codes use their registered host prefix. They do not use an `SL` diagnostic range.

## 16. Execution

### 16.1 ExecutionResult

`Execute` returns one `ExecutionResult`.

```csharp
public enum ExecutionStatus
{
    Completed,
    RuntimeFault,
    HostFault
}

public sealed class ExecutionResult
{
    public ExecutionStatus Status { get; }
    public ShellValue? Value { get; }
    public RuntimeFault? RuntimeFault { get; }
    public HostFault? HostFault { get; }
    public int CompletedStatementCount { get; }
}
```

`Value` contains the final expression value after successful execution. It is absent when the script ends with `Void` or a fault.

Exactly one fault property is present for a fault status.

### 16.2 Pre-execution checks

Before the first statement, `Execute` MUST validate:

1. The compilation belongs to the same engine.
2. The compilation is valid.
3. The catalog revision has not changed.
4. Each required session binding exists with the recorded type.
5. The session is not executing on another call.

A failed check returns a host fault and a completed statement count of zero.

### 16.3 Statement commitment

The runtime commits one assignment only after its right side completes without a runtime or host fault.

A typed `Err` completes normally and can be committed as a Result value.

A command-generated runtime fault aborts execution. The runtime does not convert it to an `Err`.

When a later statement faults, earlier assignments remain in the session. Earlier host command effects also remain.

The runtime MUST NOT execute a statement after a runtime or host fault.

### 16.4 Statement observation

`ExecutionOptions` MAY contain an `IExecutionObserver`.

The runtime calls the observer after each completed expression statement. It supplies the statement index, source span, and value.

Assignments report `Void` completion without a value.

An observer exception becomes a host fault. The runtime stops before the next statement.

### 16.5 Thread and reentrancy rules

`Compile`, metadata queries, and completion MAY run concurrently when they do not mutate the catalog or a session.

`Execute` runs synchronously on its caller thread.

The engine performs lifted work sequentially. It does not use the thread pool.

A command MUST NOT call `Execute` recursively with the active session. The engine reports this as a host fault.

## 17. Runtime error context

A declared error outcome contains a typed error value and a list of immutable context frames.

A `RuntimeFault` contains a stable code, a safe message, a source span, and immutable context frames.

```csharp
public sealed class RuntimeFault
{
    public RuntimeFaultCode Code { get; }
    public string Message { get; }
    public SourceSpan Source { get; }
    public IReadOnlyList<ErrorContextFrame> Context { get; }
}
```

A context frame can identify:

- A command or query
- A source span
- An input port or argument
- An output port
- One array index
- One member access

Array lifting appends one index for each nested array layer.

Adding a frame does not change the error's nominal ShellLang type.

A `RuntimeFault` and `HostFault` use the same context frame format. A host fault can also retain a private CLR exception.

A command-generated runtime fault receives the active invocation source span and array-index path.

## 18. Metadata, help, and completion

The engine MUST expose descriptors and compiler intrinsics through one read-only metadata model.

`HelpItem` contains at least:

- Name and symbol kind
- Description
- Input ports and their types
- Arguments, required flags, and defaults
- Output ports and the default output
- Declared error type
- Declared runtime fault codes and descriptions
- Members or enum values when applicable

`GetCompletions` MUST use the parser and static context. It SHOULD suggest:

- Visible session bindings and globals
- Commands compatible with the current pipeline type
- Valid ports and arguments
- Registered members for the receiver type
- Contextual enum members
- Intrinsics valid for the current type

A completion item contains a replacement span, insertion text, symbol kind, display type, and short description.

The host can build `help`, autocomplete, and editor features without executing a command.

## 19. Security boundary

Descriptors define the complete script-visible world.

Registration of this CLR type exposes nothing by itself:

```csharp
public sealed class Player
{
    public string Password { get; set; }
    public void InternalDeleteEverything() { }
}
```

Only registered getters, queries, and commands become accessible.

The runtime MUST NOT offer fallback reflection when name resolution fails.

The runtime MUST NOT serialize, format, or inspect an opaque host value unless a descriptor or host service supplies that behavior.

Commands can access sensitive services through `InvocationContext.Services`. The host MUST register only commands that are safe for the intended session.

ShellLang 0.1 does not define permissions inside the language. A host MAY create different engines or descriptor sets for different trust levels.

## Appendix A. Minimum host conformance cases

A conforming host test suite MUST cover these cases:

1. Reject a duplicate command name without changing the catalog.
2. Reject a descriptor that exposes `Stream<T>`.
3. Reject two default input ports or two default output ports.
4. Reject an error type with two bases or a cyclic error chain.
5. Reject null and wrong-typed values from every host boundary.
6. Convert an invoker exception to a host fault.
7. Prevent later statements after a runtime or host fault.
8. Preserve earlier assignments after a later fault.
9. Keep the old binding when a replacement assignment faults.
10. Reject execution after the catalog changes.
11. Reject execution when a required session binding changes type.
12. Permit execution when a required binding changes value but keeps its type.
13. Hide all unregistered CLR members.
14. Run lifted invocations on the caller thread in array order.
15. Evaluate command arguments once before lifted invocation.
16. Expose intrinsics through help and completion metadata.
17. Store normal Result success in `ShellResultValue.Success`.
18. Store `Result<Void,E>` success in `ShellResultValue.VoidSuccess` without a payload.
19. Reject `VoidSuccess` for a Result whose success type is not `Void`.
20. Reject construction of `Array<Void>`.
21. Lift a non-fallible zero-output operation over empty, populated, and nested arrays.
22. Produce `VoidSuccess` after an empty or fully successful fallible terminal lift.
23. Stop a fallible terminal lift at the first `Err` and preserve its index path.
24. Preserve terminal output through an outer Result.
25. Stop a lifted command at the first declared runtime fault.
26. Add the failing primary array-index path to a command runtime fault.
27. Convert an undeclared command fault code to a host fault.
28. Keep invoker exceptions classified as host faults.
29. Preserve collection-intrinsic argument evaluation order and Result propagation.
30. Preserve contextual intrinsic order, short-circuiting, first-error propagation, and array-index context.
31. Contain exceptions from equality delegates used by `contains` and `distinct` as host faults.
32. Enforce end-relative indexing, strict slice ranges, and immutable collection outputs.
