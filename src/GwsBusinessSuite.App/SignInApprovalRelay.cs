namespace GwsBusinessSuite.App;

// "Approve on my Mac" (31-10-26): a browser anywhere signs in with this Mac's Touch ID. While the
// app is open, signed in and set up for Touch ID, it waits on the server for this person's sign-in
// requests and raises RequestArrived for each new one (SignInApprovalPage shows it). Approving
// signs a fresh server challenge with this Mac's key after Touch ID and sends the number picked;
// the server checks the signature, the person and the number.
public sealed class SignInApprovalRelay(NativeSessionClient session, TouchIdKeyStore keys)
{
    private static readonly TimeSpan Backoff = TimeSpan.FromSeconds(30);
    private bool _started;
    private Guid? _shown;

    public event Action<SignInApprovalRequest>? RequestArrived;

    public string Status { get; private set; } = "Not started.";

    public void Start()
    {
        if (_started) return;
        _started = true;
        _ = LoopAsync();
    }

    private async Task LoopAsync()
    {
        // Once the Workspace tab has had time to sign in.
        await Task.Delay(TimeSpan.FromSeconds(20));
        while (true)
        {
            if (keys.Info is not { ServerKeyId: var keyId } || keyId == Guid.Empty)
            {
                Status = "Touch ID isn't set up on this Mac, so browsers can't ask it to approve a sign-in.";
                await Task.Delay(Backoff);
                continue;
            }
            // A long poll: back within about 25 seconds, with a request or without.
            var (request, error) = await session.TakeSignInRequestAsync();
            if (error is not null)
            {
                Status = $"Sign-in approvals paused: {error}";
                await Task.Delay(Backoff);
                continue;
            }
            // The same request comes back until it's answered; it's shown once.
            if (request is null || request.Id == _shown) continue;
            _shown = request.Id;
            Status = $"Sign-in request from {request.Browser} at {DateTime.Now:HH:mm}.";
            RequestArrived?.Invoke(request);
        }
    }

    // Null when the browser is approved, otherwise why not (Touch ID cancelled, the wrong number,
    // the request gone).
    public async Task<string?> ApproveAsync(SignInApprovalRequest request, int number)
    {
        if (keys.Info is not { ServerKeyId: var keyId } || keyId == Guid.Empty) return "Touch ID isn't set up on this Mac.";
        var (challenge, challengeError) = await session.GetTouchIdChallengeAsync(keyId);
        if (challenge is null) return challengeError;
        var (signature, signError) = await keys.SignAsync(challenge, $"Sign in to GWS Business Suite on {request.Browser}");
        if (signature is null) return signError ?? "Touch ID was cancelled.";
        return await session.ApproveSignInAsync(request.Id, keyId, signature, number);
    }

    public Task DeclineAsync(SignInApprovalRequest request) => session.DeclineSignInAsync(request.Id);
}
