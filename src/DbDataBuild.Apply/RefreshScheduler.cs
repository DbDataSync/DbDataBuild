namespace DbDataBuild.Apply;

/// <summary>
/// Decides when each unit of a refresh may start (docs/research/refresh-plans.md, section 6): a unit starts once every unit it depends on has finished, in the order of the plan among those that are ready, with at most
/// <c>parallel</c> units running at once. Everything but the work itself runs on the one thread that calls <see cref="RunAsync"/> (the coordinator), so the rules that decide what a unit does (a failure upstream, a
/// stop, a lock) are written as if the run were sequential: they see the units that finished before and never a half-finished one.
/// </summary>
/// <remarks>
/// With <c>parallel</c> 1 a unit starts only after the one before it finished, which is the single-threaded run of steps 1 to 4 exactly. While the run is halted (a failed load under `on_failure: stop`, or the operator)
/// every unit that has not started is admitted at once, whatever it waits for, so the host can record it as not run; the units that are running finish.
/// </remarks>
internal static class RefreshScheduler
{
    /// <param name="count">How many units; they are numbered 0 to count-1 in plan order.</param>
    /// <param name="parallel">At most this many units run at once (1 or more).</param>
    /// <param name="dependenciesOf">The units, all with a lower number, that must have finished before this one may be admitted. Asked at most once for each unit, on the coordinator.</param>
    /// <param name="halted">Whether the run is halted: nothing new may start.</param>
    /// <param name="admit">Called on the coordinator when a unit is ready and a place is free (or the run is halted). Returns the work to run on a thread of its own, or null when the host settled the unit without running it.</param>
    /// <param name="finished">Called on the coordinator after a unit was settled or its work ended, in unit order among those that ended together.</param>
    public static async Task RunAsync(int count, int parallel, Func<int, IReadOnlyList<int>> dependenciesOf, Func<bool> halted, Func<int, Task<Func<Task>?>> admit, Func<int, Task> finished, CancellationToken ct)
    {
        if (parallel < 1) throw new ArgumentOutOfRangeException(nameof(parallel));
        var state = new State[count];
        var dependencies = new IReadOnlyList<int>?[count];
        var running = new Dictionary<Task, int>();
        var remaining = count;
        Exception? failure = null;

        while (remaining > 0 && failure == null)
        {
            ct.ThrowIfCancellationRequested();
            bool changed;
            do
            {
                changed = false;
                for (var i = 0; i < count; i++)
                {
                    if (state[i] != State.Pending) continue;
                    if (!halted())
                    {
                        if (running.Count >= parallel) break;                          // no place: the units behind this one wait too, so the plan's order is kept
                        dependencies[i] ??= dependenciesOf(i);
                        if (dependencies[i]!.Any(d => state[d] != State.Done)) continue;
                    }
                    var work = await admit(i);
                    changed = true;
                    if (work == null) { state[i] = State.Done; remaining--; await finished(i); }
                    else { state[i] = State.Running; running[Task.Run(work, CancellationToken.None)] = i; }
                }
            } while (changed && remaining > 0);

            if (remaining == 0) break;
            if (running.Count == 0) throw new InvalidOperationException("The refresh stalled: units are waiting for units that will never finish.");
            await Task.WhenAny(running.Keys);
            // everything that ended while we looked, in plan order: the same set of results whatever order the threads happened to end in
            foreach (var task in running.Keys.Where(t => t.IsCompleted).OrderBy(t => running[t]).ToList())
            {
                var i = running[task];
                running.Remove(task);
                state[i] = State.Done; remaining--;
                if (task.IsFaulted) failure ??= task.Exception!.GetBaseException();
                else if (task.IsCanceled) failure ??= new OperationCanceledException();
                else await finished(i);
            }
        }

        if (failure != null)
        {
            // an exception (the operator ended the process, the log could not be written) ends the run, but not under the units that are still running: they are waited for, so no session is closed beneath a statement
            try { await Task.WhenAll(running.Keys); } catch (Exception) { /* the first failure is the one that is reported */ }
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }

    private enum State : byte { Pending, Running, Done }
}
