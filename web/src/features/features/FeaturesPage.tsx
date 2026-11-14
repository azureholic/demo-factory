import React, { useState } from 'react';
import {
  Badge,
  Button,
  Card,
  Dropdown,
  Input,
  Option,
  Textarea,
  Text,
  tokens
} from '@fluentui/react-components';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { api } from '../../api/client';
import { EmptyState, PageSkeleton, StatusPill } from '../../components/SharedComponents';

export const FeaturesPage: React.FC<{ projectId?: string; repoId?: string }> = ({ projectId, repoId }) => {
  const qc = useQueryClient();
  const [wizardOpen, setWizardOpen] = useState(false);
  const [wizardStep, setWizardStep] = useState<number>(1);
  const [title, setTitle] = useState('');
  const [specMd, setSpecMd] = useState('');
  const [activeFeatureId, setActiveFeatureId] = useState<string | null>(null);
  const [increment, setIncrement] = useState<any | null>(null);
  const [applySteps, setApplySteps] = useState<any[]>([]);

  const featuresQuery = useQuery({
    queryKey: ['features', projectId],
    queryFn: () => api.getFeatures(projectId!),
    enabled: Boolean(projectId)
  });

  const existingBoardQuery = useQuery({
    queryKey: ['board', repoId, ''],
    queryFn: () => api.getBoard(repoId!),
    enabled: Boolean(repoId && wizardOpen)
  });

  const createMutation = useMutation({
    mutationFn: () => api.createFeature(projectId!, { title, specMarkdown: specMd }),
    onSuccess: res => {
      setActiveFeatureId(res.feature.id);
      setWizardStep(2);
    }
  });

  const planIncrementMutation = useMutation({
    mutationFn: () => api.clarifyFeature(activeFeatureId!, {}, true),
    onSuccess: res => {
      setIncrement(res.increment);
      setWizardStep(3);
    }
  });

  const approveIncrementMutation = useMutation({
    mutationFn: () => api.approveFeature(activeFeatureId!),
    onSuccess: res => {
      setApplySteps(res.steps ?? []);
      setWizardStep(4);
      qc.invalidateQueries({ queryKey: ['features', projectId] });
      qc.invalidateQueries({ queryKey: ['board'] });
      qc.invalidateQueries({ queryKey: ['requirements', projectId] });
    }
  });

  const withdrawMutation = useMutation({
    mutationFn: (id: string) => api.withdrawFeature(id),
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: ['features', projectId] });
      qc.invalidateQueries({ queryKey: ['board'] });
    }
  });

  if (!projectId) {
    return <EmptyState title="No project selected" description="Create or select a project to manage feature increments." />;
  }
  if (featuresQuery.isLoading) return <PageSkeleton cards={3} />;

  const features = featuresQuery.data ?? [];
  const existingCards = existingBoardQuery.data?.cards ?? [];

  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: 16 }}>
      <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center' }}>
        <div>
          <Text size={600} weight="bold" block>
            Feature Specifications & Increments (§5.5)
          </Text>
          <Text size={200} style={{ color: tokens.colorNeutralForeground3 }}>
            Add scope to a running project without repeating repository bootstrap or disturbing work in flight.
          </Text>
        </div>
        <Button
          appearance="primary"
          onClick={() => {
            setWizardOpen(true);
            setWizardStep(1);
            setTitle('');
            setSpecMd('');
          }}
        >
          + Add Feature
        </Button>
      </div>

      {wizardOpen && (
        <Card style={{ padding: 20, border: `2px solid ${tokens.colorBrandStroke1}`, display: 'flex', flexDirection: 'column', gap: 12 }}>
          <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center' }}>
            <Text size={400} weight="bold">
              Add Feature Wizard — Step {wizardStep} of 4
            </Text>
            <Button size="small" appearance="subtle" onClick={() => setWizardOpen(false)}>
              Close
            </Button>
          </div>

          {wizardStep === 1 && (
            <>
              <Input
                placeholder="Feature title (e.g. Substation Live Heatmap)"
                value={title}
                onChange={(_, d) => setTitle(d.value)}
              />
              <Textarea
                rows={7}
                placeholder="Feature specification in Markdown (bullet requirements)..."
                value={specMd}
                onChange={(_, d) => setSpecMd(d.value)}
              />
              <div style={{ display: 'flex', justifyContent: 'flex-end' }}>
                <Button
                  appearance="primary"
                  disabled={!title.trim() || !specMd.trim() || createMutation.isPending}
                  onClick={() => createMutation.mutate()}
                >
                  Analyze Against Running Project →
                </Button>
              </div>
            </>
          )}

          {wizardStep === 2 && (
            <>
              <Text>
                PM agent checked your feature spec against existing requirements, open issues, and public-safe hygiene.
              </Text>
              <div style={{ display: 'flex', justifyContent: 'flex-end' }}>
                <Button appearance="primary" onClick={() => planIncrementMutation.mutate()}>
                  Generate Plan Increment →
                </Button>
              </div>
            </>
          )}

          {wizardStep === 3 && increment && (
            <div style={{ display: 'flex', flexDirection: 'column', gap: 12 }}>
              <Text weight="semibold">
                Increment Review — New items highlighted; existing backlog dimmed (§14.2 Screen 0a)
              </Text>
              {increment.overlapWarnings?.length > 0 && (
                <div style={{ padding: 10, borderRadius: 6, background: tokens.colorPaletteYellowBackground1 }}>
                  {increment.overlapWarnings.map((w: string, idx: number) => (
                    <Text key={idx} size={200} block>
                      ⚠ {w}
                    </Text>
                  ))}
                </div>
              )}

              {/* Dimmed existing backlog */}
              <div style={{ opacity: 0.48, display: 'flex', gap: 8, flexWrap: 'wrap' }}>
                {existingCards.map(c => (
                  <Badge key={c.id} appearance="outline">
                    Existing #{c.issueNumber}: {c.title.slice(0, 32)} ({c.boardStatus})
                  </Badge>
                ))}
              </div>

              {/* Highlighted new increment items */}
              {increment.items.map((it: any, idx: number) => (
                <div
                  key={it.key}
                  style={{
                    padding: 12,
                    borderRadius: 8,
                    border: `2px solid ${tokens.colorBrandStroke1}`,
                    background: tokens.colorNeutralBackground2,
                    display: 'flex',
                    justifyContent: 'space-between',
                    alignItems: 'center',
                    gap: 8
                  }}
                >
                  <div>
                    <Badge appearance="filled" color="brand" style={{ marginRight: 8 }}>
                      NEW · {it.key}
                    </Badge>
                    <Text weight="semibold">{it.title}</Text>
                    <Text size={200} style={{ display: 'block', color: tokens.colorNeutralForeground3 }}>
                      {it.requirementIds.join(', ')} · Size: {it.size}
                    </Text>
                  </div>
                  <Dropdown
                    value={it.initialStatus ?? 'Ready'}
                    onOptionSelect={(_, d) => {
                      const nextItems = increment.items.map((x: any, i: number) =>
                        i === idx ? { ...x, initialStatus: d.optionValue } : x
                      );
                      setIncrement({ ...increment, items: nextItems });
                    }}
                  >
                    <Option value="Ready">Ready</Option>
                    <Option value="Backlog">Backlog</Option>
                  </Dropdown>
                </div>
              ))}

              <div style={{ display: 'flex', justifyContent: 'flex-end' }}>
                <Button
                  appearance="primary"
                  disabled={approveIncrementMutation.isPending}
                  onClick={() => approveIncrementMutation.mutate()}
                >
                  Approve & Apply Increment (Spec PR + Board Items) →
                </Button>
              </div>
            </div>
          )}

          {wizardStep === 4 && (
            <div style={{ display: 'flex', flexDirection: 'column', gap: 8 }}>
              <Text weight="bold">Increment Applied Without Restart</Text>
              {applySteps.map((s: any) => (
                <div key={s.stepNumber} style={{ display: 'flex', justifyContent: 'space-between' }}>
                  <Text>{s.name}</Text>
                  <StatusPill status={s.status} />
                </div>
              ))}
              <Button appearance="primary" onClick={() => setWizardOpen(false)}>
                Done
              </Button>
            </div>
          )}
        </Card>
      )}

      {/* Existing features list */}
      <div style={{ display: 'grid', gridTemplateColumns: 'repeat(auto-fit, minmax(340px, 1fr))', gap: 14 }}>
        {features.map((f: any) => (
          <Card key={f.id} style={{ padding: 16, display: 'flex', flexDirection: 'column', gap: 10 }}>
            <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center' }}>
              <Text size={400} weight="semibold">
                {f.title}
              </Text>
              <StatusPill status={f.state} />
            </div>
            <Text size={200} style={{ color: tokens.colorNeutralForeground3 }}>
              Slug: <code>feature:{f.slug}</code> · Path: <code>{f.path}</code> · Spec v{f.specVersion}
            </Text>
            <Text size={200}>
              Issues Done: {f.doneIssues} / {f.totalIssues} · Requirements Passing: {f.passingRequirementCount} /{' '}
              {f.requirementCount} {f.epicIssueNumber ? `· Epic #${f.epicIssueNumber}` : ''}
            </Text>
            {f.state !== 'Withdrawn' && (
              <div>
                <Button size="small" appearance="subtle" onClick={() => withdrawMutation.mutate(f.id)}>
                  Withdraw Feature
                </Button>
              </div>
            )}
          </Card>
        ))}
      </div>
    </div>
  );
};
