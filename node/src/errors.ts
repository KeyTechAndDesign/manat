export type ManatErrorCode =
    | 'invalid_request'
    | 'invalid_signature'
    | 'provider_error'
    | 'network_error'
    | 'unsupported_operation';

export class ManatError extends Error {
    readonly code: ManatErrorCode;
    readonly provider: string | undefined;
    readonly providerCode: string | undefined;
    readonly providerMessage: string | undefined;
    readonly raw: unknown;

    constructor(
        code: ManatErrorCode,
        message: string,
        details: {provider?: string; providerCode?: string; providerMessage?: string; raw?: unknown; cause?: unknown} = {},
    ) {
        super(message, details.cause === undefined ? undefined : {cause: details.cause});
        this.name = 'ManatError';
        this.code = code;
        this.provider = details.provider;
        this.providerCode = details.providerCode;
        this.providerMessage = details.providerMessage;
        this.raw = details.raw;
    }
}

export function isManatError(error: unknown, code?: ManatErrorCode): error is ManatError {
    return error instanceof ManatError && (code === undefined || error.code === code);
}
