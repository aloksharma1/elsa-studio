using Elsa.Api.Client.Resources.ActivityDescriptors.Models;
using Elsa.Api.Client.Resources.WorkflowInstances.Models;
using Elsa.Studio.Workflows.Designer.Services;
using Elsa.Studio.Workflows.Domain.Contracts;
using Elsa.Studio.Workflows.Pages.WorkflowInstances.View.Models;
using Elsa.Studio.Workflows.UI.Contracts;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web.Virtualization;

namespace Elsa.Studio.Workflows.Pages.WorkflowInstances.View.Components;

public partial class Journal
{
    public Journal()
    {
        PendingActionsQueue = new PendingActionsQueue(() => new(VirtualizeComponent != null));
    }

    [Inject] private IWorkflowInstanceService WorkflowInstanceService { get; set; } = default!;
    [Inject] private IActivityRegistry ActivityRegistry { get; set; } = default!;
    [Inject] private IActivityDisplaySettingsRegistry ActivityDisplaySettingsRegistry { get; set; } = default!;

    private PendingActionsQueue PendingActionsQueue { get; }
    private WorkflowInstance? WorkflowInstance { get; set; }
    private IDictionary<string, ActivityDescriptor>? ActivityDescriptors { get; set; }
    private TimeMetricMode TimeMetricMode { get; set; } = TimeMetricMode.Relative;
    private Virtualize<JournalEntry>? VirtualizeComponent { get; set; }

    public async Task SetWorkflowInstanceAsync(WorkflowInstance workflowInstance)
    {
        WorkflowInstance = workflowInstance;
        await EnsureActivityDescriptorsAsync();
        await PendingActionsQueue.EnqueueAsync(async () => await VirtualizeComponent!.RefreshDataAsync());
    }

    protected override async Task OnInitializedAsync()
    {
        await EnsureActivityDescriptorsAsync();
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender)
        {
            await PendingActionsQueue.ProcessAsync();

            // A little hack to ensure the journal is refreshed.
            // Sometimes the journal doesn't update on first load, until a UI refresh is triggered.
            // We do it a few times, first quickly, but if that was too soon, try it again a few times, but slower.
            foreach (var timeout in new[] { 10, 100, 500, 1000 })
            {
                _ = new Timer(_ => { InvokeAsync(StateHasChanged); }, null, timeout, Timeout.Infinite);
            }
        }
    }

    private async Task EnsureActivityDescriptorsAsync()
    {
        if (ActivityDescriptors != null)
            return;

        var activities = await ActivityRegistry.ListAsync();
        ActivityDescriptors = activities.ToDictionary(x => x.TypeName);
    }

    private TimeSpan GetTimeMetric(ExecutionLogRecord current, ExecutionLogRecord? previous)
    {
        return TimeMetricMode switch
        {
            TimeMetricMode.Relative => previous == null ? TimeSpan.Zero : current.Timestamp - previous.Timestamp,
            TimeMetricMode.Absolute => SumExecutionTime(current),
            _ => throw new ArgumentOutOfRangeException()
        };
    }

    private TimeSpan SumExecutionTime(ExecutionLogRecord current) => current.Timestamp - WorkflowInstance!.CreatedAt;

    private void OnTimeMetricButtonToggleChanged(bool value)
    {
        TimeMetricMode = value ? TimeMetricMode.Absolute : TimeMetricMode.Relative;
    }

    private async ValueTask<ItemsProviderResult<JournalEntry>> FetchExecutionLogRecordsAsync(ItemsProviderRequest request)
    {
        if (WorkflowInstance == null)
            return new ItemsProviderResult<JournalEntry>(Enumerable.Empty<JournalEntry>(), 0);

        await EnsureActivityDescriptorsAsync();

        var take = request.Count == 0 ? 10 : request.Count;
        var skip = request.StartIndex > 0 ? request.StartIndex - 1 : 0;
        var response = await WorkflowInstanceService.GetJournalAsync(WorkflowInstance.Id, skip, take);
        var totalCount = request.StartIndex > 0 ? response.TotalCount - 1 : response.TotalCount;
        var records = response.Items.ToArray();
        var activityDescriptors = ActivityDescriptors!;
        var localSkip = request.StartIndex > 0 ? 1 : 0;
        var entries = records.Skip(localSkip).Select((record, index) =>
        {
            var previousIndex = index - 1;
            var previousRecord = previousIndex >= 0 ? records[previousIndex] : default;
            var activityDescriptor = activityDescriptors[record.ActivityType];
            var activityDisplaySettings = ActivityDisplaySettingsRegistry.GetSettings(record.ActivityType);
            var isEven = index % 2 == 0;
            var timeMetric = GetTimeMetric(record, previousRecord);

            return new JournalEntry(
                record,
                activityDescriptor,
                activityDisplaySettings,
                isEven,
                timeMetric);
        }).ToList();

        return new ItemsProviderResult<JournalEntry>(entries, (int)totalCount);
    }
}