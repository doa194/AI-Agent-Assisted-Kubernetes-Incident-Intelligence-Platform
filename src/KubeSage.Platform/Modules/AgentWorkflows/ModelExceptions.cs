namespace KubeSage.Platform.Modules.AgentWorkflows;

// Two distinct ways the AI layer can fail. They are kept apart because the
// right response differs: an unreachable server should be retried later once
// it is back, whereas unusable output usually means the prompt or the schema
// needs attention and retrying immediately will just fail the same way.
//
// Both are recorded as the investigation's failure reason, so an operator can
// tell "the model was down" from "the model answered nonsense" without reading
// through logs.

// The model server could not be reached, or refused the request.
public sealed class ModelUnavailableException : Exception
{
    public ModelUnavailableException(string message) : base(message) { }

    public ModelUnavailableException(string message, Exception inner) : base(message, inner) { }
}

// The model answered, but not in a shape the platform can use: empty content,
// malformed JSON, or output that did not match the requested schema.
public sealed class ModelOutputException : Exception
{
    public ModelOutputException(string message) : base(message) { }

    public ModelOutputException(string message, Exception inner) : base(message, inner) { }
}
