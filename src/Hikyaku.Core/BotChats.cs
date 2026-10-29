namespace Hikyaku.Core;

/// <summary>What BotChats needs from the gateway — BookahHermes BotChatBackend.</summary>
public interface IBotChatBackend
{
    Task<BotSession?> BotChatAsync(string profile, CancellationToken ct = default);
    Task<string> StartBotChatAsync(string profile, CancellationToken ct = default);
}

public sealed class BotChatTakenException() : Exception("The Bot Chat was just started elsewhere.");

/// <summary>
/// The profile was not made a bot, so its permanent chat must not be opened or started.
/// The session titled "Bot Chat", when one already exists, is left unchanged.
/// </summary>
public sealed class ProfileIsNotABotException(string profile)
    : Exception($"'{profile}' is a profile, not a bot. Its Bot Chat was not opened or changed.");

public sealed class BotChatUnavailableException(string message, Exception? cause = null)
    : Exception(message, cause);

/// <summary>
/// Canonical Bot Chat open — BookahHermes BotChats.kt.
/// Failed/empty lookups for known chats never start a second chat.
/// </summary>
public sealed class BotChats(IBotChatBackend backend)
{
    private readonly HashSet<string> _known = new(StringComparer.Ordinal);
    private readonly Dictionary<string, SemaphoreSlim> _locks = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    public Task NoteAsync(IEnumerable<Bot> roster, CancellationToken ct = default)
    {
        lock (_gate)
        {
            foreach (var b in roster)
                if (b.CanonicalSession?.OpenId is not null)
                    _known.Add(b.Name);
        }
        return Task.CompletedTask;
    }

    public Task ForgetAsync(string name, CancellationToken ct = default)
    {
        lock (_gate) _known.Remove(name);
        return Task.CompletedTask;
    }

    public async Task<string> OpenAsync(Bot bot, CancellationToken ct = default)
    {
        // A display name, a hidden or pinned flag, or a canonical "Bot Chat" row is not an opt-in.
        // Refuse before any lookup or session.create, so that session is never started or retitled.
        if (!bot.HasBotIdentity)
            throw new ProfileIsNotABotException(bot.Name);

        if (bot.CanonicalSession?.OpenId is not null)
        {
            lock (_gate) _known.Add(bot.Name);
        }
        var sem = GetLock(bot.Name);
        await sem.WaitAsync(ct).ConfigureAwait(false);
        try { return await ResolveAsync(bot, ct).ConfigureAwait(false); }
        finally { sem.Release(); }
    }

    private SemaphoreSlim GetLock(string name)
    {
        lock (_gate)
        {
            if (!_locks.TryGetValue(name, out var s))
                _locks[name] = s = new SemaphoreSlim(1, 1);
            return s;
        }
    }

    private async Task<string> ResolveAsync(Bot bot, CancellationToken ct)
    {
        var found = await LookupAsync(bot, ct).ConfigureAwait(false);
        if (found is not null) return found;

        bool known;
        lock (_gate) known = _known.Contains(bot.Name);
        if (known)
            throw new BotChatUnavailableException(
                $"Couldn't find {bot.Label}'s chat right now. Try again in a moment.");

        try
        {
            var started = await backend.StartBotChatAsync(bot.Name, ct).ConfigureAwait(false);
            lock (_gate) _known.Add(bot.Name);
            return started;
        }
        catch (OperationCanceledException) { throw; }
        catch (BotChatTakenException)
        {
            return await LookupAsync(bot, ct).ConfigureAwait(false)
                ?? throw new BotChatUnavailableException(
                    $"Couldn't open {bot.Label}'s chat. Try again in a moment.");
        }
        catch (Exception ex)
        {
            throw new BotChatUnavailableException(
                $"Couldn't start {bot.Label}'s chat. {ex.Message}".Trim(), ex);
        }
    }

    private async Task<string?> LookupAsync(Bot bot, CancellationToken ct)
    {
        try
        {
            var session = await backend.BotChatAsync(bot.Name, ct).ConfigureAwait(false);
            return session?.OpenId;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            throw new BotChatUnavailableException(
                $"Couldn't check {bot.Label}'s chat. Try again in a moment.", ex);
        }
    }
}
