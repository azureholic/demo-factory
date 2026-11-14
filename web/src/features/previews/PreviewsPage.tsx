import React, { useState } from 'react';
import {
  Badge,
  Button,
  Card,
  Text,
  tokens
} from '@fluentui/react-components';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { ProjectSummary, api } from '../../api/client';
import { EmptyState, PageSkeleton, StatusPill } from '../../components/SharedComponents';

export const PreviewsPage: React.FC<{ project?: ProjectSummary; repoId?: string }> = ({ project, repoId }) => {
  const qc = useQueryClient();
  const [showcaseOpen, setShowcaseOpen] = useState(false);

  const previewsQuery = useQuery({ queryKey: ['previews'], queryFn: api.getPreviews });
  const reqsQuery = useQuery({
    queryKey: ['requirements', project?.id],
    queryFn: () => api.getRequirements(project!.id),
    enabled: Boolean(project?.id && showcaseOpen)
  });

  const deleteMutation = useMutation({
    mutationFn: (id: string) => api.deletePreview(id),
    onSuccess: () => qc.invalidateQueries({ queryKey: ['previews'] })
  });

  const stopLineDrillMutation = useMutation({
    mutationFn: () => api.simulateLineFailure(repoId!, 'Post-merge smoke test regression on main', [101], false),
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: ['repos'] });
      qc.invalidateQueries({ queryKey: ['factoryStatus'] });
      qc.invalidateQueries({ queryKey: ['board'] });
    }
  });

  if (previewsQuery.isLoading) return <PageSkeleton cards={3} />;
  const previews = previewsQuery.data ?? [];

  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: 16 }}>
      <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center', flexWrap: 'wrap', gap: 12 }}>
        <div>
          <Text size={600} weight="bold" block>
            Previews, Demo Environment &amp; Andon Drills
          </Text>
          <Text size={200} style={{ color: tokens.colorNeutralForeground3 }}>
            Scale-to-zero Azure Container Apps deployed by digest from trusted workflow_run; swept after 72h or PR close.
          </Text>
        </div>
        <div style={{ display: 'flex', gap: 8 }}>
          <Button appearance="primary" onClick={() => setShowcaseOpen(v => !v)}>
            {showcaseOpen ? 'Hide Showcase Split View' : 'Showcase Demo Mode'}
          </Button>
          {repoId && (
            <Button appearance="secondary" onClick={() => stopLineDrillMutation.mutate()}>
              Simulate Red Main (Stop-the-Line Drill)
            </Button>
          )}
        </div>
      </div>

      {showcaseOpen && project && (
        <Card
          style={{
            padding: 16,
            border: `2px solid ${tokens.colorBrandStroke1}`,
            display: 'grid',
            gridTemplateColumns: '1.2fr 1fr',
            gap: 16
          }}
        >
          <div style={{ display: 'flex', flexDirection: 'column', gap: 8 }}>
            <Text size={400} weight="bold">
              Live Demo Showcase — {project.name}
            </Text>
            <Text size={200} style={{ color: tokens.colorNeutralForeground3 }}>
              Stable Demo Endpoint: <code>{project.demoUrl ?? 'https://demo-app.azurecontainerapps.io'}</code>
            </Text>
            <div
              style={{
                padding: 20,
                borderRadius: 8,
                background: tokens.colorNeutralBackground2,
                minHeight: 160,
                display: 'flex',
                flexDirection: 'column',
                justifyContent: 'center',
                gap: 8
              }}
            >
              <Badge appearance="filled" color="success" style={{ width: 'fit-content' }}>
                main GREEN · Synthetic Seed Active
              </Badge>
              <Text>
                Every merged PR automatically updates this zero-secret demo Container App via OIDC federated identity.
              </Text>
              {project.demoUrl && (
                <Button size="small" appearance="primary" as="a" href={project.demoUrl} target="_blank" style={{ width: 'fit-content' }}>
                  Launch Demo URL ↗
                </Button>
              )}
            </div>
          </div>

          <div style={{ display: 'flex', flexDirection: 'column', gap: 6 }}>
            <Text weight="semibold">Verified Requirement Coverage</Text>
            {(reqsQuery.data ?? []).map((r: any) => (
              <div
                key={r.id}
                style={{
                  padding: 8,
                  borderRadius: 6,
                  background: tokens.colorNeutralBackground2,
                  display: 'flex',
                  justifyContent: 'space-between'
                }}
              >
                <Text size={200}>
                  <strong>{r.code}</strong>: {r.text.slice(0, 52)}...
                </Text>
                <StatusPill status={r.status} />
              </div>
            ))}
          </div>
        </Card>
      )}

      {previews.length === 0 ? (
        <EmptyState
          title="No active PR preview environments"
          description="Previews are created automatically for non-draft PRs from trusted in-repo branches and deleted when the PR closes."
        />
      ) : (
        <div style={{ display: 'grid', gridTemplateColumns: 'repeat(auto-fit, minmax(340px, 1fr))', gap: 14 }}>
          {previews.map((p: any) => (
            <Card key={p.id} style={{ padding: 16, display: 'flex', flexDirection: 'column', gap: 10 }}>
              <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center' }}>
                <Text weight="semibold">
                  {p.appName} (PR #{p.prNumber})
                </Text>
                <StatusPill status={p.state} />
              </div>
              <Text size={200} style={{ color: tokens.colorNeutralForeground3 }}>
                Repo: {p.repoFullName} · Age: {p.ageMinutes}m · SHA: {p.headSha.slice(0, 8)}
              </Text>
              <Text size={100} style={{ fontFamily: 'monospace', color: tokens.colorNeutralForeground3 }}>
                Digest: {p.imageDigest.slice(0, 38)}...
              </Text>
              <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center' }}>
                <Button size="small" appearance="primary" as="a" href={p.url} target="_blank">
                  Open Preview ↗
                </Button>
                <Button size="small" appearance="subtle" onClick={() => deleteMutation.mutate(p.id)}>
                  Delete Preview
                </Button>
              </div>
            </Card>
          ))}
        </div>
      )}
    </div>
  );
};
