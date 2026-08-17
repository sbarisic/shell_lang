# ShellLang Event-System Design Contract

## 1. Status and scope

This document is the normative design for a future ShellLang event runtime. The current assembly does not define the types named here and does not compile or execute event pipelines.

The future implementation MUST add event behavior as a distinct long-lived execution mode. It MUST NOT change the synchronous one-shot meaning of `ShellEngine.Execute`.

The terms **MUST**, **MUST NOT**, **SHOULD**, and **MAY** define conformance requirements.

## 2. Future public concepts

The implementation will define these concepts:

- `EventDescriptor` describes one registered hot event source and its payload type.
- `EventSink` is the host callback used to offer payloads to one subscription.
- `EventExecutionOptions` selects queue capacity and a `TaskScheduler`.
- `EventExecutionHandle` reports lifetime, completion, failure, and disposal.
- `ShellEngine.Subscribe` starts one validated event compilation against one session.

Exact constructors and convenience overloads remain an implementation detail until these types are added. The semantic requirements in this document are fixed.

## 3. Compilation model

An event compilation is distinct from an ordinary one-shot compilation. `Execute` MUST reject an event compilation, and `Subscribe` MUST reject an ordinary compilation.

An event script contains exactly one expression pipeline. Its root MUST be one registered event source. Assignments, additional statements, and event sources nested inside arguments or other expressions are invalid.

An event has one registered payload type. The payload becomes the primary input to the first downstream operation and follows the existing direct connection, Result propagation, default-output projection, scalar lifting, and terminal `Void` rules.

Events are not first-class values. A script cannot assign an event, pass one as an argument, store one in an array or Result, combine event sources, or expose one through `Stream<T>`.

For illustration only, a future source could be written as `player_joined -> inventory::give_item(item: StarterKit)`. This is plain text, not an executable 0.1 example.

## 4. Host-owned hot sources

An `EventDescriptor` represents a hot source owned by the host. Subscribing asks its provider to attach one `EventSink` and return an unsubscribe mechanism. The provider MUST NOT be discovered through reflection.

The source can emit only its declared payload type. The engine validates every accepted payload through the registered ShellLang descriptor and CLR adapter before delivery.

A provider exception while attaching or detaching, a null or invalid payload, or any other source-contract violation terminates the handle as failed.

## 5. Session lease and isolation

Each subscription exclusively leases one `ShellSession` for its complete lifetime. While leased:

- External `ShellEngine.Execute` with that session MUST be rejected.
- Another `Subscribe` with that session MUST be rejected.
- Public binding addition, replacement, and removal MUST be rejected.
- Existing bindings are captured as read-only inputs for every delivery.
- Commands can still mutate host-owned state exposed by their descriptors.

The lease begins before the host source is attached. It ends only after the handle reaches a terminal state and cleanup has completed. A failed attempt to attach the source releases the lease.

Each subscription requires a distinct session. Multiple subscriptions can use the same engine and descriptors, but they have independent bindings, queues, execution state, and terminal results.

## 6. Acceptance and queueing

Every handle owns one thread-safe FIFO queue. `EventExecutionOptions` configures its positive capacity; the default is 64 accepted emissions.

The host sink accepts an emission only while the handle is active and capacity is available. Acceptance establishes ownership of that payload and its position in the per-subscription FIFO order.

The sink MUST NOT block waiting for capacity and MUST NOT silently discard an accepted emission. If an active source offers an emission when the queue is full, overflow terminates the handle as failed and immediately starts unsubscription. The overflowing payload is not accepted.

Emissions offered after disposal or terminal failure are rejected and never enter the queue.

## 7. Scheduling and serial delivery

One configurable `TaskScheduler` processes accepted emissions. The default is `TaskScheduler.Default`.

The handle schedules serial work only. At most one delivery for a subscription can execute at a time. Accepted payload order is preserved for that subscription, including when the host emits concurrently from several threads.

Different subscriptions have no relative ordering guarantee and can run concurrently on their independently selected schedulers.

Each delivery runs the compiled pipeline synchronously once. Existing expression evaluation order, contextual `this` scoping, array-index paths, command effects, and observer boundaries apply within that delivery.

## 8. Delivery results and failure

A typed `Err` is a normal completed delivery result. It does not terminate the subscription and does not prevent the next queued payload from running.

Any runtime fault or host fault terminates the handle as failed. Invalid payloads, provider exceptions, scheduler failures, and queue overflow also terminate it as failed. Once failure begins, the handle accepts no further emissions and unsubscribes the host source.

The failure record MUST preserve the same safe diagnostic, source span, typed context frames, and complete array-index path available to one-shot execution. Successfully completed earlier deliveries and host effects remain committed.

The first terminal cause wins. Later cleanup failures MAY be retained for trusted host diagnostics but MUST NOT replace the primary cause.

## 9. Disposal and completion

`EventExecutionHandle` implements `IDisposable` and `IAsyncDisposable`.

Synchronous disposal MUST:

1. Stop accepting emissions.
2. Request host unsubscription immediately.
3. Discard queued, not-yet-started emissions.
4. Allow an active synchronous delivery to finish without interruption.
5. Return without waiting for that active delivery.

Asynchronous disposal performs the same transition and then waits until active delivery, unsubscription, cleanup, and session-lease release finish.

Disposal is idempotent. Normal disposal completes the handle without failure. A failure that started before disposal remains the terminal result.

The engine does not inject cancellation into synchronous commands. Hosts that require cancellable work must expose their own safe command contract in a later version.

## 10. Arrays, backpressure, and permissions

An event payload can be an ordinary registered array type. Array lifting occurs inside one delivery and does not create parallel work or additional queue entries. A runtime or host fault during a lifted invocation terminates the subscription with its full index path.

Capacity is measured in accepted event payloads, not lifted elements. The only 0.1 backpressure policy is bounded FIFO termination on overflow. Blocking producers, dropping, coalescing, replay, batching, and demand signaling are deferred.

Permissions remain catalog-based. A host exposes only event descriptors and commands safe for the intended scripts, or constructs separate engines for different trust levels. The event runtime does not add language-level permissions.

## 11. Deferred features

The first event implementation MUST NOT add `Stream<T>`, event combination, cold sources, replay, shared sessions, concurrent delivery within one subscription, language-level cancellation, or mutable event values.

Implementation requires deterministic mock-event conformance tests for attachment failure, payload validation, FIFO order, concurrent emission, scheduler selection, capacity overflow, Result continuation, runtime and host faults, array paths, disposal races, async disposal, session leasing, independent subscriptions, and catalog permissions.
