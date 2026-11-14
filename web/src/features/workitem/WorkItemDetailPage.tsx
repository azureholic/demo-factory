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
  Tab,
  TabList,
  Text,
  tokens
} from '@fluentui/react-components';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useParams } from 'react-router-dom';
import { api } from '../../api/client';
import {
  GateChecklist,
  PageSkeleton,
  SessionTimeline,
  SeverityBadge,
  StatusPill
} from '../../components/SharedComponents';

export const WorkItemDetailPage: React.FC = () => {
  const { id } = useParams<{ id: string }>();
  const qc = useQueryClient();
  const [tab, setTab] = useState<string>('gate');
  const [waiveTarget, setWaiveTarget] = useState<string | null>(null);
  const [waiveReason, setWaiveReason] = useState<string>('');
  const [changeNote, setChangeNote] = useState<string>('');

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

  const modelOverrideMutation = useMutation({
    mutationFn: (codingModel: string) => api.updateWorkItemSettings(id!, codingModel),
    onSuccess: () => qc.invalidateQueries({ queryKey: ['workitem', id] })
  });

  if (detailQuery.isLoading || !detailQuery.data) return <PageSkeleton cards={4} />;

  const { workItem: wi, sessions, rounds, findings, latestGateConditions, timeline, costSummary } = detailQuery.data;
  const models = modelsQuery.data ?? [];

  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: 16 }}>
      {/* Header & Operator Actions (§14.2 Screen 4) */}
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
                <Badge appearance="tint" color={wi.pmVerdict === 'Ready' ? 'success' : 'warning'}>
                  PM: {wi.pmVerdict}
                </Badge>
              )}
            </div>
            <Text size={200} style={{ color: tokens.colorNeutralForeground3, marginTop: 4, display: 'block' }}>
              Priority: {wi.priority} · Size: {wi.size} · Attempts: {wi.attempts} · Round {wi.currentRound}/{wi.maxRounds}{' '}
              {wi.headSha ? `· Head SHA ${wi.headSha.slice(0, 7)}` : ''}
            </Text>
          </div>

          <div style={{ display: 'flex', gap: 8, flexWrap: 'wrap' }}>
            {wi.state === 'AwaitingApproval' && (
              <>
                <Button
                  appearance="primary"
                  onClick={() => actionMutation.mutate({ action: 'approve', body: { reason: 'Approved in Work Item view' } })}
                >
                  Approve
                </Button>
                <Button
                  appearance="secondary"
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
              <Button appearance="primary" onClick={() => actionMutation.mutate({ action: 'unblock' })}>
                Unblock / Retry
              </Button>
            )}
            <Button appearance="secondary" onClick={() => actionMutation.mutate({ action: 'retry' })}>
              Retry
            </Button>
            <Button appearance="subtle" onClick={() => actionMutation.mutate({ action: 'cancel' })}>
              Cancel
            </Button>
            {wi.prUrl && (
              <Button appearance="outline" as="a" href={wi.prUrl} target="_blank">
                Open in GitHub
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

      {/* Split View: Left Spec & Acceptance Criteria | Right Tabs */}
      <div style={{ display: 'grid', gridTemplateColumns: 'minmax(300px, 1fr) minmax(420px, 1.6fr)', gap: 16 }}>
        <Card style={{ padding: 16, display: 'flex', flexDirection: 'column', gap: 12 }}>
          <Text size={400} weight="semibold">
            Issue Specification
          </Text>
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
            {wi.bodyMarkdown}
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

          {wi.blockedReason && (
            <div style={{ padding: 10, borderRadius: 6, background: tokens.colorPaletteRedBackground1 }}>
              <Text weight="semibold" block>
                Blocked / Escalation Detail
              </Text>
              <Text size={200}>{wi.blockedReason}</Text>
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
                A mandatory audit reason is required to waive a Copilot code review finding (§14.2 Screen 4).
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
