import React, { useState } from 'react';
import {
  Badge,
  Button,
  Card,
  Dialog,
  DialogActions,
  DialogBody,
  DialogContent,
  DialogSurface,
  DialogTitle,
  Dropdown,
  Input,
  Option,
  Spinner,
  Tab,
  TabList,
  Textarea,
  Text,
  tokens
} from '@fluentui/react-components';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useParams } from 'react-router-dom';
import { api } from '../../api/client';
import {
  GateChecklist,
  OperationProgressBanner,
  PageSkeleton,
  SessionTimeline,
  SeverityBadge,
  StatusPill
} from '../../components/SharedComponents';

function splitQuestions(text?: string | null): string[] {
  if (!text) return [];
  const parts = text
    .split(/(?<=\?)\s+/)
    .map(s => s.trim())
    .filter(Boolean);
  return parts.length > 0 ? parts : [text];
}

export const WorkItemDetailPage: React.FC = () => {
  const { id } = useParams<{ id: string }>();
  const qc = useQueryClient();
  const [tab, setTab] = useState<string>('gate');
  const [waiveTarget, setWaiveTarget] = useState<string | null>(null);
  const [waiveReason, setWaiveReason] = useState<string>('');
  const [changeNote, setChangeNote] = useState<string>('');
  const [clarificationText, setClarificationText] = useState<string>('');

  const [editingSpec, setEditingSpec] = useState<boolean>(false);
  const [draftTitle, setDraftTitle] = useState<string>('');
  const [draftBody, setDraftBody] = useState<string>('');
  const [draftCriteria, setDraftCriteria] = useState<string[]>([]);

  const detailQuery = useQuery({
    queryKey: ['workitem', id],
    queryFn: () => api.getWorkItem(id!),
    enabled: Boolean(id)
  });

  const modelsQuery = useQuery({ queryKey: ['models'], queryFn: api.getModels });

  const actionMutation = useMutation({
    mutationFn: ({ action, body }: { action: string; body?: any }) => api.workItemAction(id!, action, body),
    onSuccess: () => {
      setWaiveTarget(null);
      setWaiveReason('');
      qc.invalidateQueries({ queryKey: ['workitem', id] });
      qc.invalidateQueries({ queryKey: ['board'] });
      qc.invalidateQueries({ queryKey: ['approvals'] });
    }
  });

  const clarifyMutation = useMutation({
    mutationFn: ({ note, proceedWithDefaults }: { note: string; proceedWithDefaults?: boolean }) =>
      api.clarifyWorkItem(id!, note, Boolean(proceedWithDefaults)),
    onSuccess: () => {
      setClarificationText('');
      qc.invalidateQueries({ queryKey: ['workitem', id] });
      qc.invalidateQueries({ queryKey: ['board'] });
      qc.invalidateQueries({ queryKey: ['sessions'] });
    }
  });

  const saveSpecMutation = useMutation({
    mutationFn: () =>
      api.updateWorkItemSpec(id!, {
        title: draftTitle,
        bodyMarkdown: draftBody,
        acceptanceCriteria: draftCriteria
      }),
    onSuccess: () => {
      setEditingSpec(false);
      qc.invalidateQueries({ queryKey: ['workitem', id] });
      qc.invalidateQueries({ queryKey: ['board'] });
    }
  });

  const modelOverrideMutation = useMutation({
    mutationFn: (codingModel: string) => api.updateWorkItemSettings(id!, codingModel),
    onSuccess: () => qc.invalidateQueries({ queryKey: ['workitem', id] })
  });

  if (detailQuery.isLoading || !detailQuery.data) return <PageSkeleton cards={4} />;

  const { workItem: wi, sessions, rounds, findings, latestGateConditions, timeline, costSummary } = detailQuery.data;
  const models = modelsQuery.data ?? [];
  const parsedQuestions = splitQuestions(wi.blockedReason);
  const busy = actionMutation.isPending || clarifyMutation.isPending || saveSpecMutation.isPending;

  const startEditingSpec = () => {
    setDraftTitle(wi.title ?? '');
    setDraftBody(wi.bodyMarkdown ?? '');
    setDraftCriteria(Array.isArray(wi.acceptanceCriteria) ? [...wi.acceptanceCriteria] : []);
    setEditingSpec(true);
  };

  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: 16 }}>
      {/* Header & Operator Actions */}
      <Card style={{ padding: 16 }}>
        <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'flex-start', flexWrap: 'wrap', gap: 12 }}>
          <div>
            <div style={{ display: 'flex', alignItems: 'center', gap: 8, flexWrap: 'wrap' }}>
              <Text size={500} weight="bold">
                #{wi.issueNumber} {wi.title}
              </Text>
              <StatusPill status={wi.state} />
              <Badge appearance="outline">Profile: {wi.gateProfile}</Badge>
              {wi.pmVerdict && (
                <Badge appearance="tint" color={wi.pmVerdict === 'Ready' || wi.pmVerdict === 'OperatorReady' ? 'success' : 'warning'}>
                  PM: {wi.pmVerdict === 'OperatorReady' ? 'Ready' : wi.pmVerdict}
                </Badge>
              )}
            </div>
            <Text size={200} style={{ color: tokens.colorNeutralForeground3, marginTop: 4, display: 'block' }}>
              {wi.repoFullName ? `${wi.repoFullName} · ` : ''}Priority: {wi.priority} · Effort: {wi.size} · Attempts: {wi.attempts} · Round {wi.currentRound}/{wi.maxRounds}{' '}
              {wi.headSha ? `· Head SHA ${wi.headSha.slice(0, 7)}` : ''}
            </Text>
          </div>

          <div style={{ display: 'flex', gap: 8, flexWrap: 'wrap' }}>
            {wi.state === 'AwaitingApproval' && (
              <>
                <Button
                  appearance="primary"
                  disabled={busy}
                  onClick={() => actionMutation.mutate({ action: 'approve', body: { reason: 'Approved in Work Item view' } })}
                >
                  Approve
                </Button>
                <Button
                  appearance="secondary"
                  disabled={busy}
                  onClick={() =>
                    actionMutation.mutate({
                      action: 'request-changes',
                      body: { reason: changeNote || 'Please refine implementation per review' }
                    })
                  }
                >
                  Request Changes
                </Button>
              </>
            )}
            {(wi.state === 'Blocked' || wi.state === 'Failed' || wi.state === 'NeedsClarification') && (
              <Button
                appearance="primary"
                disabled={busy}
                onClick={() => actionMutation.mutate({ action: 'unblock' })}
              >
                Unblock / Retry
              </Button>
            )}
            <Button appearance="secondary" disabled={busy} onClick={() => actionMutation.mutate({ action: 'retry' })}>
              Retry
            </Button>
            <Button appearance="subtle" disabled={busy} onClick={() => actionMutation.mutate({ action: 'cancel' })}>
              Cancel
            </Button>
            {wi.issueUrl && (
              <Button appearance="outline" as="a" href={wi.issueUrl} target="_blank">
                Open Issue #{wi.issueNumber} in GitHub ↗
              </Button>
            )}
            {wi.prUrl && (
              <Button appearance="outline" as="a" href={wi.prUrl} target="_blank">
                Open PR #{wi.prNumber} in GitHub ↗
              </Button>
            )}
            {wi.previewUrl && (
              <Button appearance="outline" as="a" href={wi.previewUrl} target="_blank">
                Open Preview
              </Button>
            )}
          </div>
        </div>
      </Card>

      <OperationProgressBanner
        active={clarifyMutation.isPending || saveSpecMutation.isPending || actionMutation.isPending}
        title={
          clarifyMutation.isPending
            ? `Syncing clarification to GitHub Issue #${wi.issueNumber} and dispatching Copilot…`
            : saveSpecMutation.isPending
            ? `Updating specification & acceptance criteria on GitHub Issue #${wi.issueNumber}…`
            : `Updating work item #${wi.issueNumber} and triggering scheduler…`
        }
        subtitle="Changes are saved to the Demo-Factory board and mirrored directly to the GitHub issue."
      />

      {/* Split View: Left Spec & Acceptance Criteria | Right Tabs */}
      <div style={{ display: 'grid', gridTemplateColumns: 'minmax(340px, 1.15fr) minmax(420px, 1.55fr)', gap: 16 }}>
        <Card style={{ padding: 16, display: 'flex', flexDirection: 'column', gap: 12 }}>
          <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center', gap: 8 }}>
            <Text size={400} weight="semibold">
              Issue Specification & Acceptance Criteria
            </Text>
            {!editingSpec ? (
              <Button size="small" appearance="outline" onClick={startEditingSpec}>
                Edit Spec & Criteria
              </Button>
            ) : (
              <div style={{ display: 'flex', gap: 6 }}>
                <Button size="small" appearance="subtle" disabled={saveSpecMutation.isPending} onClick={() => setEditingSpec(false)}>
                  Cancel
                </Button>
                <Button
                  size="small"
                  appearance="primary"
                  icon={saveSpecMutation.isPending ? <Spinner size="tiny" /> : undefined}
                  disabled={saveSpecMutation.isPending || !draftTitle.trim()}
                  onClick={() => saveSpecMutation.mutate()}
                >
                  {saveSpecMutation.isPending ? 'Syncing…' : 'Save & Sync to GitHub'}
                </Button>
              </div>
            )}
          </div>

          {editingSpec ? (
            <div style={{ display: 'flex', flexDirection: 'column', gap: 10 }}>
              <div>
                <Text size={200} weight="semibold" block style={{ marginBottom: 4 }}>
                  Issue Title
                </Text>
                <Input value={draftTitle} onChange={(_, d) => setDraftTitle(d.value)} style={{ width: '100%' }} />
              </div>
              <div>
                <Text size={200} weight="semibold" block style={{ marginBottom: 4 }}>
                  Specification Markdown (synced to GitHub Issue #{wi.issueNumber})
                </Text>
                <Textarea
                  rows={8}
                  value={draftBody}
                  onChange={(_, d) => setDraftBody(d.value)}
                  style={{ width: '100%' }}
                />
              </div>
              <div style={{ display: 'flex', flexDirection: 'column', gap: 6 }}>
                <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center' }}>
                  <Text size={200} weight="semibold">
                    Acceptance Criteria ({draftCriteria.length})
                  </Text>
                  <Button
                    size="small"
                    appearance="subtle"
                    onClick={() => setDraftCriteria([...draftCriteria, 'New testable acceptance criterion'])}
                  >
                    + Add Criterion
                  </Button>
                </div>
                {draftCriteria.map((ac, idx) => (
                  <div key={idx} style={{ display: 'flex', gap: 6 }}>
                    <Input
                      style={{ flex: 1 }}
                      value={ac}
                      onChange={(_, d) =>
                        setDraftCriteria(draftCriteria.map((item, i) => (i === idx ? d.value : item)))
                      }
                    />
                    <Button
                      size="small"
                      appearance="subtle"
                      onClick={() => setDraftCriteria(draftCriteria.filter((_, i) => i !== idx))}
                    >
                      ✕
                    </Button>
                  </div>
                ))}
              </div>
            </div>
          ) : (
            <>
              <pre
                style={{
                  whiteSpace: 'pre-wrap',
                  fontFamily: 'inherit',
                  margin: 0,
                  padding: 12,
                  background: tokens.colorNeutralBackground2,
                  borderRadius: 6,
                  fontSize: 13
                }}
              >
                {wi.bodyMarkdown || '(No specification body yet — click Edit Spec & Criteria to add details.)'}
              </pre>

              <Text size={400} weight="semibold">
                Acceptance Criteria
              </Text>
              {wi.acceptanceCriteria.length === 0 ? (
                <Text size={200} style={{ color: tokens.colorNeutralForeground3 }}>
                  None explicitly structured (PM verdict: {wi.pmVerdict ?? 'Pending'}).
                </Text>
              ) : (
                <ul style={{ margin: 0, paddingLeft: 18 }}>
                  {wi.acceptanceCriteria.map((ac: string, idx: number) => (
                    <li key={idx} style={{ marginBottom: 6, fontSize: 13 }}>
                      {ac}
                    </li>
                  ))}
                </ul>
              )}
            </>
          )}

          {(wi.blockedReason || wi.state === 'NeedsClarification' || wi.state === 'Blocked') && (
            <div
              style={{
                padding: 14,
                borderRadius: 8,
                background: tokens.colorPaletteRedBackground1,
                border: `1px solid ${tokens.colorPaletteRedBorder1}`,
                display: 'flex',
                flexDirection: 'column',
                gap: 10
              }}
            >
              <div>
                <Text weight="semibold" block>
                  {wi.state === 'NeedsClarification'
                    ? 'Clarification Requested Before Dispatch'
                    : 'Blocked / Escalation Detail'}
                </Text>
                {parsedQuestions.length > 1 ? (
                  <ul style={{ margin: '6px 0 0 0', paddingLeft: 18 }}>
                    {parsedQuestions.map((q, idx) => (
                      <li key={idx} style={{ fontSize: 12.5, marginBottom: 4 }}>
                        {q}
                      </li>
                    ))}
                  </ul>
                ) : (
                  <Text size={200} style={{ display: 'block', marginTop: 4 }}>
                    {wi.blockedReason ?? 'Awaiting operator input or unblock.'}
                  </Text>
                )}
              </div>

              <div
                style={{
                  padding: 12,
                  borderRadius: 6,
                  background: tokens.colorNeutralBackground1,
                  display: 'flex',
                  flexDirection: 'column',
                  gap: 8
                }}
              >
                <Text size={200} weight="semibold">
                  Add Clarification or Implementation Guidance (syncs directly to GitHub Issue #{wi.issueNumber})
                </Text>
                <Textarea
                  rows={4}
                  placeholder="Answer the questions above or add implementation notes here… (or click 'Proceed With Defaults' to let Copilot apply standard template conventions)"
                  value={clarificationText}
                  onChange={(_, d) => setClarificationText(d.value)}
                />
                <div style={{ display: 'flex', gap: 8, flexWrap: 'wrap', justifyContent: 'flex-end' }}>
                  <Button
                    appearance="secondary"
                    disabled={busy}
                    onClick={() => clarifyMutation.mutate({ note: clarificationText, proceedWithDefaults: true })}
                  >
                    Proceed With Defaults →
                  </Button>
                  <Button
                    appearance="primary"
                    icon={clarifyMutation.isPending ? <Spinner size="tiny" /> : undefined}
                    disabled={busy || !clarificationText.trim()}
                    onClick={() => clarifyMutation.mutate({ note: clarificationText, proceedWithDefaults: false })}
                  >
                    {clarifyMutation.isPending ? 'Saving & Dispatching…' : 'Save Clarification & Dispatch to Copilot →'}
                  </Button>
                </div>
              </div>
            </div>
          )}

          {wi.state === 'AwaitingApproval' && (
            <Input
              placeholder="Optional note for Approve / Request Changes..."
              value={changeNote}
              onChange={(_, d) => setChangeNote(d.value)}
            />
          )}
        </Card>

        <Card style={{ padding: 16, display: 'flex', flexDirection: 'column', gap: 14 }}>
          <TabList selectedValue={tab} onTabSelect={(_, d) => setTab(String(d.value))}>
            <Tab value="gate">Gate (15 Conditions)</Tab>
            <Tab value="sessions">Sessions ({sessions.length})</Tab>
            <Tab value="rounds">Review Rounds ({rounds.length})</Tab>
            <Tab value="checks">Checks</Tab>
            <Tab value="timeline">Timeline</Tab>
            <Tab value="model">Model & Cost</Tab>
          </TabList>

          {tab === 'gate' && <GateChecklist conditions={latestGateConditions} />}

          {tab === 'sessions' && (
            <div style={{ display: 'flex', flexDirection: 'column', gap: 10 }}>
              {sessions.map((s: any) => (
                <div
                  key={s.id}
                  style={{
                    padding: 12,
                    borderRadius: 6,
                    background: tokens.colorNeutralBackground2,
                    display: 'flex',
                    justifyContent: 'space-between',
                    alignItems: 'center',
                    gap: 8
                  }}
                >
                  <div>
                    <div style={{ display: 'flex', gap: 8, alignItems: 'center' }}>
                      <Text weight="semibold">{s.mode}</Text>
                      <StatusPill status={s.state} />
                      <Badge appearance="outline">{s.model}</Badge>
                    </div>
                    <Text size={200} style={{ color: tokens.colorNeutralForeground3, display: 'block', marginTop: 4 }}>
                      Task: {s.copilotTaskId} · Tokens: {s.tokensUsed ?? '—'} · Cost: ${Number(s.estimatedCostUsd).toFixed(2)}
                    </Text>
                    {s.triggerSummary && (
                      <Text size={200} style={{ display: 'block', marginTop: 2 }}>
                        {s.triggerSummary}
                      </Text>
                    )}
                  </div>
                  {s.sessionUrl && (
                    <Button size="small" appearance="outline" as="a" href={s.sessionUrl} target="_blank">
                      Session Log
                    </Button>
                  )}
                </div>
              ))}
            </div>
          )}

          {tab === 'rounds' && (
            <div style={{ display: 'flex', flexDirection: 'column', gap: 10 }}>
              {findings.length === 0 ? (
                <Text style={{ color: tokens.colorNeutralForeground3 }}>No Copilot review findings recorded.</Text>
              ) : (
                findings.map((f: any) => (
                  <div
                    key={f.id}
                    style={{
                      padding: 12,
                      borderRadius: 6,
                      background: tokens.colorNeutralBackground2,
                      display: 'flex',
                      justifyContent: 'space-between',
                      alignItems: 'flex-start',
                      gap: 10
                    }}
                  >
                    <div>
                      <div style={{ display: 'flex', gap: 8, alignItems: 'center' }}>
                        <Text weight="semibold">{f.shortId}</Text>
                        <SeverityBadge severity={f.severity} />
                        <StatusPill status={f.status} />
                        <Text size={200} style={{ fontFamily: 'monospace' }}>
                          {f.path}{f.line ? `:${f.line}` : ''}
                        </Text>
                      </div>
                      <Text size={200} style={{ display: 'block', marginTop: 4 }}>
                        {f.body}
                      </Text>
                      {f.pmTriageClassification && (
                        <Text size={200} style={{ color: tokens.colorNeutralForeground3, display: 'block', marginTop: 4 }}>
                          PM Triage (advisory): {f.pmTriageClassification} — {f.pmTriageReason}
                        </Text>
                      )}
                      {f.waiverReason && (
                        <Text size={200} style={{ color: tokens.colorPaletteGreenForeground1, display: 'block', marginTop: 4 }}>
                          Waived by {f.waivedBy}: {f.waiverReason}
                        </Text>
                      )}
                    </div>
                    {f.status !== 'Waived' && f.status !== 'Addressed' && (
                      <Button size="small" onClick={() => setWaiveTarget(f.fingerprint)}>
                        Waive Finding
                      </Button>
                    )}
                  </div>
                ))
              )}
            </div>
          )}

          {tab === 'checks' && (
            <div style={{ display: 'flex', flexDirection: 'column', gap: 8 }}>
              {['build', 'test', 'lint', 'CodeQL', 'demo-factory/review-gate'].map(chk => (
                <div
                  key={chk}
                  style={{
                    padding: '10px 12px',
                    borderRadius: 6,
                    background: tokens.colorNeutralBackground2,
                    display: 'flex',
                    justifyContent: 'space-between'
                  }}
                >
                  <Text weight="semibold">{chk}</Text>
                  <Badge appearance="tint" color={chk === 'demo-factory/review-gate' && wi.state === 'AwaitingApproval' ? 'warning' : 'success'}>
                    {chk === 'demo-factory/review-gate' && wi.state === 'AwaitingApproval'
                      ? 'action_required (Demo-Factory App)'
                      : 'success (source-pinned)'}
                  </Badge>
                </div>
              ))}
            </div>
          )}

          {tab === 'timeline' && <SessionTimeline events={timeline} />}

          {tab === 'model' && (
            <div style={{ display: 'flex', flexDirection: 'column', gap: 14 }}>
              <div>
                <Text weight="semibold" block style={{ marginBottom: 6 }}>
                  Resolved Coding Model (Override for next session)
                </Text>
                <Dropdown
                  value={wi.codingModel}
                  onOptionSelect={(_, d) => {
                    if (d.optionValue) modelOverrideMutation.mutate(d.optionValue);
                  }}
                >
                  {models
                    .filter((m: any) => m.enabled)
                    .map((m: any) => (
                      <Option key={m.id} value={m.apiModelId}>
                        {`${m.displayName} (${m.apiModelId})`}
                      </Option>
                    ))}
                </Dropdown>
              </div>
              <div style={{ padding: 12, borderRadius: 6, background: tokens.colorNeutralBackground2 }}>
                <Text block>Total Copilot Sessions: {costSummary.sessionCount} / 5 budget</Text>
                <Text block>Total Tokens Reported: {costSummary.totalTokens.toLocaleString()}</Text>
                <Text block>Estimated Spend: ${Number(costSummary.totalEstimatedUsd).toFixed(2)} / $5.00 item cap</Text>
              </div>
            </div>
          )}
        </Card>
      </div>

      {/* Mandatory-reason Waive Finding Dialog */}
      <Dialog open={Boolean(waiveTarget)} onOpenChange={(_, d) => !d.open && setWaiveTarget(null)}>
        <DialogSurface>
          <DialogBody>
            <DialogTitle>Waive Review Finding</DialogTitle>
            <DialogContent style={{ display: 'flex', flexDirection: 'column', gap: 10 }}>
              <Text size={200}>
                A mandatory audit reason is required to waive a Copilot code review finding.
              </Text>
              <Input
                placeholder="Explain why this finding is safe to waive..."
                value={waiveReason}
                onChange={(_, d) => setWaiveReason(d.value)}
              />
            </DialogContent>
            <DialogActions>
              <Button appearance="secondary" onClick={() => setWaiveTarget(null)}>
                Cancel
              </Button>
              <Button
                appearance="primary"
                disabled={!waiveReason.trim()}
                onClick={() =>
                  actionMutation.mutate({
                    action: 'waive-finding',
                    body: { fingerprint: waiveTarget, reason: waiveReason }
                  })
                }
              >
                Confirm Waiver
              </Button>
            </DialogActions>
          </DialogBody>
        </DialogSurface>
      </Dialog>
    </div>
  );
};
