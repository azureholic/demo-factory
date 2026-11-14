export type BoardStatus = 'Backlog' | 'Ready' | 'InProgress' | 'InReview' | 'Blocked' | 'Done';
export type AutonomyLevel = 'L0' | 'L1' | 'L2';
export type PriorityLevel = 'Low' | 'Medium' | 'High' | 'Critical';
export type ItemSize = 'Small' | 'Medium' | 'Large' | 'XLarge';

export interface CurrentUser {
  login: string;
  role: 'Viewer' | 'Operator' | 'Admin';
  themePreference: 'system' | 'light' | 'dark';
  densityPreference: 'comfortable' | 'compact';
  copilotPlan: string;
  connection: {
    gitHubLogin: string;
    connected: boolean;
    isRevoked: boolean;
    accessTokenExpiresAt?: string;
    refreshTokenExpiresAt?: string;
    reconnectRequired: boolean;
  };
}

export interface FactoryStatusDto {
  status: 'Running' | 'Paused' | 'Degraded';
  globalPaused: boolean;
  anyLineStopped: boolean;
  reconnectRequired: boolean;
  profile: string;
  ownerLogin: string;
}

export interface ProjectSummary {
  id: string;
  name: string;
  slug: string;
  state: string;
  currentSpecVersion: number;
  templateId: string;
  templateVersion: string;
  defaultAutonomy: AutonomyLevel;
  defaultCodingModel: string;
  isPublic: boolean;
  demoUrl?: string;
  createdAt: string;
  repoId?: string;
  repoFullName?: string;
  newInboxCount: number;
}

export interface RepoSummary {
  id: string;
  projectId: string;
  owner: string;
  name: string;
  fullName: string;
  gitHubProjectId: string;
  gitHubProjectNumber: number;
  policyReference: string;
  paused: boolean;
  pauseReason?: string;
  readinessPassed: boolean;
  lineStatus: 'Running' | 'LineStopped';
  lineCause?: string;
  inFlightCount: number;
  readyQueueDepth: number;
  blockedCount: number;
  newInboxCount: number;
}

export interface PlannedBacklogItem {
  key: string;
  title: string;
  description: string;
  acceptanceCriteria: string[];
  requirementIds: string[];
  size: ItemSize;
  priority: PriorityLevel;
  milestone: string;
  isAcceptanceTest: boolean;
  blockedByKeys: string[];
  blockedByExistingIssueNumbers: number[];
  predictedTouchPaths: string[];
  initialStatus: BoardStatus;
}

export interface GeneratedPlan {
  adr0001Markdown: string;
  requirements: { id: string; text: string }[];
  milestones: string[];
  items: PlannedBacklogItem[];
  estimatedCostUsd: number;
  estimatedDurationMinutes: number;
}

export interface BoardCardDto {
  id: string;
  issueNumber: number;
  title: string;
  state: string;
  boardStatus: BoardStatus;
  priority: PriorityLevel;
  size: ItemSize;
  passesWorkLabelRule: boolean;
  isAcceptanceTestIssue: boolean;
  gateProfile: string;
  featureSlug?: string;
  currentRound: number;
  maxRounds: number;
  codingModel: string;
  prNumber?: number;
  prUrl?: string;
  previewUrl?: string;
  blockedReason?: string;
  blockedByCount: number;
  blockedByIssues: number[];
  unblocksCount: number;
  hasLiveSession: boolean;
  liveSessionState?: string;
}

export interface GateConditionDto {
  number: number;
  name: string;
  applicable: boolean;
  passed: boolean;
  isPending: boolean;
  summary: string;
}

async function request<T>(path: string, init?: RequestInit): Promise<T> {
  const resp = await fetch(path, {
    credentials: 'include',
    headers: {
      'Content-Type': 'application/json',
      ...(init?.headers ?? {})
    },
    ...init
  });
  if (!resp.ok) {
    const errBody = await resp.json().catch(() => ({ error: resp.statusText }));
    throw new Error(errBody.error || `HTTP ${resp.status}`);
  }
  if (resp.status === 204) return undefined as T;
  return resp.json() as Promise<T>;
}

export const api = {
  getMe: () => request<CurrentUser>('/auth/me'),
  savePreferences: (prefs: { themePreference?: string; densityPreference?: string }) =>
    request('/auth/preferences', { method: 'PUT', body: JSON.stringify(prefs) }),
  revokeGitHub: () => request('/auth/revoke', { method: 'POST' }),
  getFactoryStatus: () => request<FactoryStatusDto>('/api/factory/status'),
  pauseFactory: (repoId?: string) =>
    request(`/api/factory:pause${repoId ? `?repo=${repoId}` : ''}`, { method: 'POST' }),
  resumeFactory: (repoId?: string) =>
    request(`/api/factory:resume${repoId ? `?repo=${repoId}` : ''}`, { method: 'POST' }),

  getMetrics: () => request<any>('/api/metrics'),
  getApprovals: () => request<{ workItemApprovals: any[]; changeProposals: any[] }>('/api/approvals'),
  getTemplates: () => request<any[]>('/api/templates'),
  getProjects: () => request<ProjectSummary[]>('/api/projects'),
  getProject: (id: string) => request<any>(`/api/projects/${id}`),
  createProject: (payload: any) => request<any>('/api/projects', { method: 'POST', body: JSON.stringify(payload) }),
  clarifyProject: (id: string, answers: Record<string, string>, dismissPublicSafeFlags = false) =>
    request<any>(`/api/projects/${id}:clarify`, { method: 'POST', body: JSON.stringify({ answers, dismissPublicSafeFlags }) }),
  getProjectPlan: (id: string) => request<GeneratedPlan>(`/api/projects/${id}/plan`),
  saveProjectPlan: (id: string, plan: GeneratedPlan) =>
    request<GeneratedPlan>(`/api/projects/${id}/plan`, { method: 'PUT', body: JSON.stringify(plan) }),
  approveProject: (id: string) => request<any>(`/api/projects/${id}:approve`, { method: 'POST' }),
  getProjectBootstrap: (id: string) => request<any>(`/api/projects/${id}/bootstrap`),

  getFeatures: (projectId: string) => request<any[]>(`/api/projects/${projectId}/features`),
  createFeature: (projectId: string, payload: any) =>
    request<any>(`/api/projects/${projectId}/features`, { method: 'POST', body: JSON.stringify(payload) }),
  createFeatureFromIssues: (projectId: string, issueNumbers: number[]) =>
    request<any>(`/api/projects/${projectId}/features:from-issues`, { method: 'POST', body: JSON.stringify({ issueNumbers }) }),
  clarifyFeature: (featureId: string, answers: Record<string, string>, dismissPublicSafeFlags = false) =>
    request<any>(`/api/features/${featureId}:clarify`, { method: 'POST', body: JSON.stringify({ answers, dismissPublicSafeFlags }) }),
  getFeaturePlan: (featureId: string) => request<any>(`/api/features/${featureId}/plan`),
  saveFeaturePlan: (featureId: string, plan: any) =>
    request<any>(`/api/features/${featureId}/plan`, { method: 'PUT', body: JSON.stringify(plan) }),
  approveFeature: (featureId: string) => request<any>(`/api/features/${featureId}:approve`, { method: 'POST' }),
  withdrawFeature: (featureId: string) => request<any>(`/api/features/${featureId}:withdraw`, { method: 'POST' }),
  getFeatureStatus: (featureId: string) => request<any>(`/api/features/${featureId}/status`),

  getInbox: (projectId: string) => request<any[]>(`/api/projects/${projectId}/inbox`),
  dismissInboxItem: (inboxId: string) => request<any>(`/api/inbox/${inboxId}:dismiss`, { method: 'POST' }),

  getSpec: (projectId: string) => request<any>(`/api/projects/${projectId}/spec`),
  updateSpec: (projectId: string, specMarkdown: string, dismissPublicSafeFlags = false) =>
    request<any>(`/api/projects/${projectId}/spec`, { method: 'PUT', body: JSON.stringify({ specMarkdown, dismissPublicSafeFlags }) }),
  getRequirements: (projectId: string) => request<any[]>(`/api/projects/${projectId}/requirements`),
  getChangeProposals: (projectId: string) => request<any[]>(`/api/projects/${projectId}/change-proposals`),
  applyChangeProposal: (proposalId: string) => request<any>(`/api/change-proposals/${proposalId}:apply`, { method: 'POST' }),

  getRepos: () => request<RepoSummary[]>('/api/repos'),
  onboardRepo: (payload: any) => request<any>('/api/repos', { method: 'POST', body: JSON.stringify(payload) }),
  getRepoReadiness: (repoId: string) => request<any>(`/api/repos/${repoId}/readiness`),
  fixRepoReadiness: (repoId: string) => request<any>(`/api/repos/${repoId}/readiness:fix`, { method: 'POST' }),
  getRepoLine: (repoId: string) => request<any>(`/api/repos/${repoId}/line`),
  resumeRepoLine: (repoId: string) => request<any>(`/api/repos/${repoId}/line:resume`, { method: 'POST' }),
  simulateLineFailure: (repoId: string, cause: string, suspectPrNumbers: number[], flakeRetryPassed = false) =>
    request<any>(`/api/repos/${repoId}/line:simulate-failure`, {
      method: 'POST',
      body: JSON.stringify({ cause, suspectPrNumbers, flakeRetryPassed })
    }),

  getBoard: (repoId: string, feature?: string) =>
    request<{ repoId: string; repoFullName: string; lockedColumns: BoardStatus[]; cards: BoardCardDto[] }>(
      `/api/repos/${repoId}/board${feature ? `?feature=${encodeURIComponent(feature)}` : ''}`
    ),
  getGraph: (repoId: string) => request<any>(`/api/repos/${repoId}/graph`),
  getWorkItem: (id: string) => request<any>(`/api/workitems/${id}`),
  workItemAction: (id: string, action: string, body?: any) =>
    request<any>(`/api/workitems/${id}:${action}`, { method: 'POST', body: body ? JSON.stringify(body) : undefined }),
  updateWorkItemSettings: (id: string, codingModel: string | null) =>
    request<any>(`/api/workitems/${id}/settings`, { method: 'PUT', body: JSON.stringify({ codingModel }) }),

  getSessions: (params?: { repoId?: string; mode?: string; state?: string; model?: string }) => {
    const sp = new URLSearchParams();
    if (params?.repoId) sp.set('repoId', params.repoId);
    if (params?.mode) sp.set('mode', params.mode);
    if (params?.state) sp.set('state', params.state);
    if (params?.model) sp.set('model', params.model);
    const qs = sp.toString();
    return request<any[]>(`/api/sessions${qs ? `?${qs}` : ''}`);
  },
  getPreviews: () => request<any[]>('/api/previews'),
  deletePreview: (id: string) => request<void>(`/api/previews/${id}`, { method: 'DELETE' }),

  getModels: () => request<any[]>('/api/models'),
  saveModel: (id: string, payload: any) => request<any>(`/api/models/${id}`, { method: 'PUT', body: JSON.stringify(payload) }),
  getSettings: () => request<any>('/api/settings'),
  saveGlobalSettings: (payload: any) => request<any>('/api/settings', { method: 'PUT', body: JSON.stringify(payload) }),
  getRepoSettings: (repoId: string) => request<any>(`/api/repos/${repoId}/settings`),
  saveRepoSettings: (repoId: string, payload: any) =>
    request<any>(`/api/repos/${repoId}/settings`, { method: 'PUT', body: JSON.stringify(payload) }),
  getRepoPolicy: (repoId: string) => request<any>(`/api/repos/${repoId}/policy`),

  getAudit: (search?: string) =>
    request<any[]>(`/api/audit${search ? `?search=${encodeURIComponent(search)}` : ''}`)
};
