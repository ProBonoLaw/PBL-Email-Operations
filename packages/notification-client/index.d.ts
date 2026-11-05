export type WorkflowStatus = 'STARTED' | 'SUCCESS' | 'FAILED';
/** PRELIMINARY: general ingestion completed; EVIDENCE_EXTRACTION: evidence extraction completed. */
export type IngestionStatus = WorkflowStatus | 'PRELIMINARY' | 'EVIDENCE_EXTRACTION';
export type ArgumentStatus = WorkflowStatus | 'INITIAL_ARGUMENT' | 'COUNTER_ARGUMENT' |
  'FINAL_ARGUMENT' | 'FACT_VALIDATION' | 'REVISION';

export interface AccountData { accountId: string; }
export interface WorkflowData extends AccountData {
  sessionId: string;
  status: WorkflowStatus;
  requestText?: string;
  errorMessage?: string;
  requestId?: string;
}
export interface DeclarationData extends WorkflowData { declarationUrl: string; }
export interface ArgumentBuilderData extends Omit<WorkflowData, 'status'> { status: ArgumentStatus; }
export interface DocumentData extends AccountData { filepath: string; status: WorkflowStatus; errorMessage?: string; }
export interface IngestionData extends Omit<DocumentData, 'status'> { status: IngestionStatus; }
export interface CaseData extends AccountData { status: WorkflowStatus; errorMessage?: string; }
export interface AlertData extends AccountData {
  sourceId: string | number;
  clientFeedbackRequestId: string;
  markdownPath: string;
  status: 'FEEDBACK_REQUIRED';
}
export interface Notice<TType extends string, TData> {
  oid: string;
  id?: string | null;
  notificationType: TType;
  data: TData;
}
export type Notification =
  | Notice<'DECLARATION', DeclarationData>
  | Notice<'ARGUMENT_BUILDER', ArgumentBuilderData>
  | Notice<'TIMELINE_BUILDER', WorkflowData>
  | Notice<'INGESTION', IngestionData>
  | Notice<'EGESTION', DocumentData>
  | Notice<'CASE_CREATED' | 'CASE_DELETED', CaseData>
  | Notice<'ALERT', AlertData>
  | Notice<'CHAT_MESSAGE', unknown>;

export class NotificationTransmissionError extends Error {
  constructor(status: number, responseBody: string);
  readonly status: number;
  readonly responseBody: string;
}
export class NotificationClient {
  constructor(baseUrl: string, options?: { fetch?: typeof globalThis.fetch });
  send(notice: Notification, options?: { signal?: AbortSignal }): Promise<void>;
}
