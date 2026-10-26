import {ManatError} from './errors.js';

export type FetchLike = (input: string, init?: RequestInit) => Promise<Response>;

export interface HttpOptions {
    fetch?: FetchLike;
    /** Per-request timeout in milliseconds. Default 30 000. */
    timeoutMs?: number;
}

export interface HttpRequest {
    method: 'GET' | 'POST' | 'PUT' | 'DELETE';
    url: string;
    headers?: Record<string, string>;
    /** JSON body (object), form body (URLSearchParams) or raw string. */
    body?: unknown;
}

export interface HttpResponse {
    status: number;
    headers: Headers;
    text: string;
}

export class HttpClient {
    private readonly fetchImpl: FetchLike;
    private readonly timeoutMs: number;

    constructor(private readonly provider: string, options: HttpOptions = {}) {
        const impl = options.fetch ?? globalThis.fetch;
        if (!impl) throw new ManatError('invalid_request', 'No fetch implementation available; pass options.fetch (Node 18+ has one built in)');
        this.fetchImpl = impl;
        this.timeoutMs = options.timeoutMs ?? 30_000;
    }

    async send(request: HttpRequest): Promise<HttpResponse> {
        const headers: Record<string, string> = {...request.headers};
        let body: string | undefined;
        if (request.body instanceof URLSearchParams) {
            body = request.body.toString();
            headers['content-type'] ??= 'application/x-www-form-urlencoded';
        } else if (typeof request.body === 'string') {
            body = request.body;
        } else if (request.body !== undefined) {
            body = JSON.stringify(request.body);
            headers['content-type'] ??= 'application/json';
        }

        const controller = new AbortController();
        const timer = setTimeout(() => controller.abort(), this.timeoutMs);
        try {
            const init: RequestInit = {method: request.method, headers, signal: controller.signal};
            if (body !== undefined) init.body = body;
            const response = await this.fetchImpl(request.url, init);
            return {status: response.status, headers: response.headers, text: await response.text()};
        } catch (error) {
            const reason = controller.signal.aborted ? `timed out after ${this.timeoutMs} ms` : (error as Error)?.message ?? 'request failed';
            throw new ManatError('network_error', `${this.provider}: ${request.method} ${request.url} ${reason}`, {provider: this.provider, cause: error});
        } finally {
            clearTimeout(timer);
        }
    }

    async json<T = unknown>(request: HttpRequest): Promise<{status: number; data: T}> {
        const response = await this.send({...request, headers: {accept: 'application/json', ...request.headers}});
        try {
            return {status: response.status, data: (response.text ? JSON.parse(response.text) : null) as T};
        } catch (error) {
            throw new ManatError('network_error', `${this.provider}: expected JSON, got HTTP ${response.status} "${response.text.slice(0, 200)}"`, {
                provider: this.provider,
                raw: response.text,
                cause: error,
            });
        }
    }
}
