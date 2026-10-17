namespace SemanticRouter.AspNetCore;

/// <summary>
/// Deletes activity and everything else that holds text from the deleted prompts and replies: the feedback about those
/// entries (the judge's reason can quote them), the prompt previews learned changes quote as evidence, and the latest
/// connector failure messages (a failed CLI run's output). The activity goes last, so the state database empties its
/// files once, after every delete.
/// </summary>
internal sealed class ActivityPurge(RoutingActivityLog activity, RoutingFeedbackLog feedback, ITuningStore tuning, ConnectorIncidents incidents)
{
    /// <summary>Deletes the activity with <paramref name="since"/> ≤ timestamp &lt; <paramref name="until"/> and what came from it.</summary>
    public ActivityPurgeResult Run(DateTimeOffset since, DateTimeOffset until) =>
        activity.Purge(since, until, ids =>
        {
            feedback.Forget(ids);

            // A change quotes prompts that were labelled before it was made; it does not say which entries they came from.
            foreach (TuningChange change in tuning.Changes().Where(c => c.Timestamp >= since))
            {
                TuningChange scrubbed = change.WithoutPromptText();
                if (!ReferenceEquals(scrubbed, change))
                {
                    tuning.Save(scrubbed);
                }
            }

            incidents.ForgetDetails();
        });
}
