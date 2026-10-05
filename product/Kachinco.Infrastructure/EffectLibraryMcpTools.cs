using System.Text;
using System.Text.Json;
using Flamoris.Mcp.Core;
using Kachinco.Core;

namespace Kachinco.Infrastructure;

// Explicit application opt-in; tools cannot change the root or name arbitrary filesystem paths.
public static class EffectLibraryMcpTools
{
    public static IReadOnlyList<HostTool> Create(EditorSession session, EffectLibrary library, Action changed)
    {
        JsonElement Json(object value) => JsonSerializer.SerializeToElement(value, EffectLibrary.Json);
        JsonElement Commit(Result<EditBatch> plan, CancellationToken token)
        {
            if (!plan.Success) return Json(plan);
            var result = session.Execute(plan.Value!, token);
            if (result.Success && !plan.Value!.DryRun) { try { changed(); } catch { } }
            return Json(result);
        }
        return [
            new HostTool<EmptyInput>("effect_list", "List bounded local Effect Library definitions; browsing executes no code.", Schema<EmptyInput>(),
                OperationKind.Query, Decode<EmptyInput>, (context, _, token) => context.ReadAsync(() => Json(library.List())), foreground: false),
            new HostTool<IdInput>("effect_get", "Inspect a saved reusable definition by stable ID.", Schema<IdInput>(),
                OperationKind.Query, Decode<IdInput>, (context, input, token) => context.ReadAsync(() => Json(library.Get(input.Id))), foreground: false),
            new HostTool<SaveInput>("effect_save", "Create or update a validated local effect; expectedVersion is required to overwrite. Requires Effect Library opt-in and Edit permission.",
                Schema<SaveInput>(), OperationKind.Mutation, Decode<SaveInput>, async (context, input, token) => {
                    var validated = await library.ValidateProgramAsync(input.Effect, token);
                    if (!validated.Success) return await context.ReadAsync(() => Json(validated));
                    return await context.CommitAsync(() => Json(library.SaveValidated(validated.Value!, input.ExpectedVersion)));
                }),
            new HostTool<CaptureInput>("effect_capture", "Save the selected visual clip's visible automation interval as a reusable named effect.",
                Schema<CaptureInput>(), OperationKind.Mutation, Decode<CaptureInput>,
                (context, input, token) => context.CommitAsync(() => {
                    var effect = EffectComposition.Capture(session.GetProject(), input.SequenceId, input.ClipId, input.Name);
                    return Json(effect.Success ? library.SaveValidated(effect.Value!) : effect);
                })),
            new HostTool<DeleteInput>("effect_delete", "Delete one local effect with an expected item version; applied project edits remain intact.",
                Schema<DeleteInput>(), OperationKind.Mutation, Decode<DeleteInput>,
                (context, input, token) => context.CommitAsync(() => Json(library.Delete(input.Id, input.ExpectedVersion)))),
            new HostTool<ApplyInput>("effect_apply", "Apply visual automation to clipId, or bind a restricted Recipe to clapperId. Recipe output uses ordinary explicit generation. Shared Undo/Redo; supports dryRun.",
                Schema<ApplyInput>(), OperationKind.Transaction, Decode<ApplyInput>, async (context, input, token) => {
                    var inspected = await context.ReadAsync(() => Json(library.Get(input.Id)));
                    var item = inspected.Deserialize<Result<EffectDefinition>>(EffectLibrary.Json)!;
                    if (!item.Success) return await context.ReadAsync(() => Json(item));
                    var validated = await library.ValidateProgramAsync(item.Value!, token);
                    if (!validated.Success) return await context.ReadAsync(() => Json(validated));
                    return await context.CommitAsync(() => {
                        // Plan and commit under the same host serialization lane and request guard.
                        var current = library.Get(input.Id);
                        if (!current.Success || JsonSerializer.Serialize(current.Value, EffectLibrary.Json) != JsonSerializer.Serialize(item.Value, EffectLibrary.Json))
                            return Json(Result<EditBatch>.Fail(Diagnostic.Error("EFFECT_VERSION_CONFLICT", "Effect changed during preparation.")));
                        Result<EditBatch> plan;
                        if (input.ClipId is { } clip && input.ClapperId is null)
                            plan = EffectComposition.Plan(session.GetProject(), input.SequenceId, clip, item.Value!, input.Parameters);
                        else if (input.ClapperId is { } clapper && input.ClipId is null && input.Parameters is null)
                            plan = EffectComposition.PlanRecipe(session.GetProject(), input.SequenceId, clapper, item.Value!);
                        else return Json(Result<EditBatch>.Fail(Diagnostic.Error("EFFECT_TARGET_REQUIRED", "Use exactly one clipId or clapperId; Recipe parameters are defined by its bounded source.")));
                        if (plan.Success) plan = Result<EditBatch>.Ok(plan.Value! with { DryRun = input.DryRun });
                        return Commit(plan, token);
                    });
                })
        ];
    }
    private sealed record EmptyInput;
    private sealed record IdInput(Guid Id);
    private sealed record SaveInput(EffectDefinition Effect, int? ExpectedVersion = null);
    private sealed record DeleteInput(Guid Id, int ExpectedVersion);
    private sealed record CaptureInput(Guid SequenceId, Guid ClipId, string Name);
    private sealed record ApplyInput(Guid Id, Guid SequenceId, Guid? ClipId = null, Guid? ClapperId = null,
        EffectParameters? Parameters = null, bool DryRun = false);
    private static JsonElement Schema<T>() => JsonSerializer.SerializeToElement(McpTypedSchema.Describe(typeof(T)));
    private static T Decode<T>(JsonElement input)
    {
        McpTypedSchema.Validate(typeof(T), input);
        return input.Deserialize<T>(EffectLibrary.Json) ?? throw new JsonException();
    }
}
