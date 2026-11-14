import React, { useEffect, useState } from 'react';
import {
  Badge,
  Button,
  Card,
  Textarea,
  Text,
  tokens
} from '@fluentui/react-components';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { api } from '../../api/client';
import { EmptyState, PageSkeleton, StatusPill } from '../../components/SharedComponents';

export const SpecPage: React.FC<{ projectId?: string }> = ({ projectId }) => {
  const qc = useQueryClient();
  const [draftSpec, setDraftSpec] = useState('');
  const [errorMsg, setErrorMsg] = useState<string | null>(null);

  const specQuery = useQuery({
    queryKey: ['spec', projectId],
    queryFn: () => api.getSpec(projectId!),
    enabled: Boolean(projectId)
  });

  const reqsQuery = useQuery({
    queryKey: ['requirements', projectId],
    queryFn: () => api.getRequirements(projectId!),
    enabled: Boolean(projectId)
  });

  const proposalsQuery = useQuery({
    queryKey: ['changeProposals', projectId],
    queryFn: () => api.getChangeProposals(projectId!),
    enabled: Boolean(projectId)
  });

  useEffect(() => {
    if (specQuery.data?.specMarkdown) {
      setDraftSpec(specQuery.data.specMarkdown);
    }
  }, [specQuery.data?.specMarkdown]);

  const saveSpecMutation = useMutation({
    mutationFn: () => api.updateSpec(projectId!, draftSpec),
    onSuccess: () => {
      setErrorMsg(null);
      qc.invalidateQueries({ queryKey: ['spec', projectId] });
      qc.invalidateQueries({ queryKey: ['changeProposals', projectId] });
      qc.invalidateQueries({ queryKey: ['approvals'] });
    },
    onError: (e: Error) => setErrorMsg(e.message)
  });

  const applyProposalMutation = useMutation({
    mutationFn: (id: string) => api.applyChangeProposal(id),
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: ['changeProposals', projectId] });
      qc.invalidateQueries({ queryKey: ['board'] });
      qc.invalidateQueries({ queryKey: ['approvals'] });
    }
  });

  if (!projectId) {
    return <EmptyState title="No project selected" description="Select a project to inspect its living specification." />;
  }
  if (specQuery.isLoading) return <PageSkeleton cards={3} />;

  const reqs = reqsQuery.data ?? [];
  const versions = specQuery.data?.versions ?? [];
  const proposals = proposalsQuery.data ?? [];

  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: 16 }}>
      <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center', flexWrap: 'wrap', gap: 12 }}>
        <div>
          <Text size={600} weight="bold" block>
            Living Specification &amp; Traceability
          </Text>
          <Text size={200} style={{ color: tokens.colorNeutralForeground3 }}>
            Current tag: <code>spec-v{specQuery.data?.currentSpecVersion}</code> · Protected path enforced by review gate
          </Text>
        </div>
        <Button
          appearance="primary"
          disabled={saveSpecMutation.isPending}
          onClick={() => saveSpecMutation.mutate()}
        >
          Save Spec Edit & Propose Backlog Delta
        </Button>
      </div>

      {errorMsg && (
        <Card style={{ padding: 12, background: tokens.colorPaletteRedBackground1 }}>
          <Text weight="semibold">{errorMsg}</Text>
        </Card>
      )}

      <div style={{ display: 'grid', gridTemplateColumns: '1.3fr 1fr', gap: 16 }}>
        <Card style={{ padding: 16, display: 'flex', flexDirection: 'column', gap: 12 }}>
          <Text weight="semibold">docs/spec.md Editor & Highlighted Preview</Text>
          <Textarea
            rows={14}
            value={draftSpec}
            onChange={(_, d) => setDraftSpec(d.value)}
            style={{ fontFamily: 'monospace' }}
          />
          <div
            style={{
              padding: 12,
              borderRadius: 8,
              background: tokens.colorNeutralBackground2,
              whiteSpace: 'pre-wrap',
              fontSize: 13
            }}
          >
            {draftSpec.split(/(REQ-\d{3,})/g).map((part, idx) =>
              /^REQ-\d{3,}$/.test(part) ? (
                <Badge key={idx} appearance="filled" color="brand" style={{ margin: '0 2px' }}>
                  {part}
                </Badge>
              ) : (
                <span key={idx}>{part}</span>
              )
            )}
          </div>
        </Card>

        <div style={{ display: 'flex', flexDirection: 'column', gap: 14 }}>
          <Card style={{ padding: 16, display: 'flex', flexDirection: 'column', gap: 10 }}>
            <Text weight="semibold">Requirement Coverage Matrix</Text>
            {reqs.map((r: any) => (
              <div
                key={r.id}
                style={{
                  padding: 10,
                  borderRadius: 6,
                  background: tokens.colorNeutralBackground2,
                  display: 'flex',
                  flexDirection: 'column',
                  gap: 4
                }}
              >
                <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center' }}>
                  <Badge appearance="tint" color="brand">
                    {r.code}
                  </Badge>
                  <StatusPill status={r.status} />
                </div>
                <Text size={200}>{r.text}</Text>
                <Text size={100} style={{ color: tokens.colorNeutralForeground3 }}>
                  Issues: {r.linkedIssues.map((n: number) => `#${n}`).join(', ') || '—'} · ATs:{' '}
                  {r.linkedAcceptanceTests.join(', ') || '—'}
                </Text>
              </div>
            ))}
          </Card>

          <Card style={{ padding: 16, display: 'flex', flexDirection: 'column', gap: 8 }}>
            <Text weight="semibold">Change Proposals & Version History</Text>
            {proposals.map((cp: any) => (
              <div
                key={cp.id}
                style={{
                  padding: 10,
                  borderRadius: 6,
                  background: tokens.colorNeutralBackground2,
                  display: 'flex',
                  justifyContent: 'space-between',
                  alignItems: 'center'
                }}
              >
                <div>
                  <Text size={200} weight="semibold" block>
                    v{cp.fromSpecVersion} → v{cp.toSpecVersion} ({cp.kind})
                  </Text>
                  <Text size={200} style={{ color: tokens.colorNeutralForeground3 }}>
                    {cp.summary}
                  </Text>
                </div>
                {cp.state === 'PendingApproval' ? (
                  <Button size="small" appearance="primary" onClick={() => applyProposalMutation.mutate(cp.id)}>
                    Apply
                  </Button>
                ) : (
                  <StatusPill status={cp.state} />
                )}
              </div>
            ))}
            {versions.map((v: any) => (
              <Text key={v.id} size={200} style={{ color: tokens.colorNeutralForeground3 }}>
                Tag <code>{v.tag}</code> · SHA {v.gitSha.slice(0, 8)} · {new Date(v.createdAt).toLocaleString()}
              </Text>
            ))}
          </Card>
        </div>
      </div>
    </div>
  );
};
