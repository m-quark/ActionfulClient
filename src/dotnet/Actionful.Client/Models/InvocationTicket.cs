namespace MQuark.Actionful.Client;

/// <summary>
/// Handle returned by <see cref="IActionfulClient.SubmitAsync(string,System.Threading.CancellationToken)"/>
/// representing an in-flight job.
/// <para>
/// <b>JobId</b> — unique job identifier extracted from the server-assigned Location URL.<br/>
/// <b>PollUrl</b> — absolute URL to use when polling for job status.<br/>
/// <b>SubmittedAt</b> — UTC timestamp when the job was submitted.
/// </para>
/// </summary>
public sealed record InvocationTicket(string JobId, string PollUrl, DateTimeOffset SubmittedAt);
