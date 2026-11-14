import React, { useMemo, useState } from 'react';
import {
  Badge,
  Button,
  Card,
  Checkbox,
  Dropdown,
  Input,
  Option,
  Textarea,
  Text,
  tokens
} from '@fluentui/react-components';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useNavigate } from 'react-router-dom';
import { Background, Edge, MarkerType, Node, ReactFlow } from '@xyflow/react';
import { GeneratedPlan, PlannedBacklogItem, api } from '../../api/client';
import { StatusPill } from '../../components/SharedComponents';

export const NewProjectWizardPage: React.FC = () => {
  const navigate = useNavigate();
  const qc = useQueryClient();

  const [step, setStep] = useState<number>(1);
  const [name, setName] = useState('Contoso Fleet Optimizer Demo');
  const [specMarkdown, setSpecMarkdown] = useState(
    `# Contoso Fleet Optimizer Demo\n\nInteractive EV fleet dispatch and charging-station telemetry demo.\n\n## Goals\n- Display live EV depot charge state and route readiness cards using synthetic fleet data.\n- Expose GET /api/depots and POST /api/dispatch/optimize REST endpoints.\n- Provide dark/light mode Fluent dashboard with CSV schedule export.`
  );
  const [templateId, setTemplateId] = useState('dotnet-react');
  const [autonomy, setAutonomy] = useState<'L0' | 'L1' | 'L2'>('L1');
  const [codingModel, setCodingModel] = useState('gemini-4-carbon');

  const [projectId, setProjectId] = useState<string | null>(null);
  const [questions, setQuestions] = useState<string[]>([]);
  const [answers, setAnswers] = useState<Record<string, string>>({});
  const [publicSafe, setPublicSafe] = useState<{ passed: boolean; flags: any[] }>({ passed: true, flags: [] });
  const [dismissFlags, setDismissFlags] = useState(false);

  const [plan, setPlan] = useState<GeneratedPlan | null>(null);
  const [bootstrapSteps, setBootstrapSteps] = useState<any[]>([]);
  const [errorMsg, setErrorMsg] = useState<string | null>(null);

  const templatesQuery = useQuery({ queryKey: ['templates'], queryFn: api.getTemplates });
  const modelsQuery = useQuery({ queryKey: ['models'], queryFn: api.getModels });

  const intakeMutation = useMutation({
    mutationFn: () =>
      api.createProject({
        name,
        specMarkdown,
        templateId,
        autonomy,
        codingModel,
        deployTarget: 'aca-previews-and-demo',
        isPublic: true
      }),
    onSuccess: res => {
      setErrorMsg(null);
      setProjectId(res.project.id);
      setQuestions(res.clarifyingQuestions ?? []);
      setPublicSafe(res.publicSafe ?? { passed: true, flags: [] });
      setStep(2);
    },
    onError: (e: Error) => setErrorMsg(e.message)
  });

  const clarifyAndPlanMutation = useMutation({
    mutationFn: () => api.clarifyProject(projectId!, answers, dismissFlags),
    onSuccess: res => {
      setErrorMsg(null);
      setPlan(res.plan);
      setStep(4);
    },
    onError: (e: Error) => setErrorMsg(e.message)
  });

  const savePlanMutation = useMutation({
    mutationFn: (nextPlan: GeneratedPlan) => api.saveProjectPlan(projectId!, nextPlan),
    onSuccess: updated => setPlan(updated)
  });

  const approveAndLaunchMutation = useMutation({
    mutationFn: () => api.approveProject(projectId!),
    onSuccess: res => {
      setErrorMsg(null);
      setBootstrapSteps(res.bootstrapSteps ?? []);
      setStep(5);
      qc.invalidateQueries({ queryKey: ['projects'] });
      qc.invalidateQueries({ queryKey: ['repos'] });
      qc.invalidateQueries({ queryKey: ['board'] });
    },
    onError: (e: Error) => setErrorMsg(e.message)
  });

  const handleFileUpload = async (e: React.ChangeEvent<HTMLInputElement>) => {
    const file = e.target.files?.[0];
    if (!file) return;
    const text = await file.text();
    setSpecMarkdown(prev => `${prev}\n\n${text}`);
  };

  const updateItem = (idx: number, patch: Partial<PlannedBacklogItem>) => {
    if (!plan) return;
    const items = plan.items.map((it, i) => (i === idx ? { ...it, ...patch } : it));
    const next = {
      ...plan,
      items,
      estimatedCostUsd: Number((items.length * 0.65).toFixed(2)),
      estimatedDurationMinutes: items.length * 14
    };
    setPlan(next);
  };

  const deleteItem = (idx: number) => {
    if (!plan) return;
    const removedKey = plan.items[idx].key;
    const items = plan.items
      .filter((_, i) => i !== idx)
      .map(it => ({ ...it, blockedByKeys: it.blockedByKeys.filter(k => k !== removedKey) }));
    setPlan({
      ...plan,
      items,
      estimatedCostUsd: Number((items.length * 0.65).toFixed(2)),
      estimatedDurationMinutes: items.length * 14
    });
  };

  const planGraph = useMemo(() => {
    const items = plan?.items ?? [];
    const nodes: Node[] = items.map((it, idx) => ({
      id: it.key,
      position: { x: (idx % 2) * 240 + 20, y: Math.floor(idx / 2) * 105 + 20 },
      data: { label: `${it.key}: ${it.title.slice(0, 34)}` },
      style: {
        width: 215,
        fontSize: 11,
        borderRadius: 6,
        border: it.isAcceptanceTest ? '2px solid #8b5cf6' : '2px solid #2b82f0',
        background: tokens.colorNeutralBackground1,
        color: tokens.colorNeutralForeground1
      }
    }));
    const edges: Edge[] = items.flatMap(it =>
      it.blockedByKeys.map(b => ({
        id: `${b}->${it.key}`,
        source: b,
        target: it.key,
        markerEnd: { type: MarkerType.ArrowClosed }
      }))
    );
    return { nodes, edges };
  }, [plan]);

  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: 16 }}>
      <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center' }}>
        <div>
          <Text size={600} weight="bold" block>
            New Project Wizard
          </Text>
          <Text size={200} style={{ color: tokens.colorNeutralForeground3 }}>
            Step {step} of 5 · Spec → Clarification & Public-Safe Check → Stack & Policy → Plan Review → Bootstrap Launch
          </Text>
        </div>
        <div style={{ display: 'flex', gap: 6 }}>
          {[1, 2, 3, 4, 5].map(n => (
            <Badge key={n} appearance={step === n ? 'filled' : 'outline'} color={step >= n ? 'brand' : 'informative'}>
              {n}
            </Badge>
          ))}
        </div>
      </div>

      {errorMsg && (
        <Card style={{ padding: 12, background: tokens.colorPaletteRedBackground1 }}>
          <Text weight="semibold">{errorMsg}</Text>
        </Card>
      )}

      {step === 1 && (
        <Card style={{ padding: 20, display: 'flex', flexDirection: 'column', gap: 14 }}>
          <Text size={400} weight="semibold">
            1. Project Name & Specification (Markdown)
          </Text>
          <Input
            value={name}
            onChange={(_, d) => setName(d.value)}
            placeholder="Project name (e.g. Contoso Fleet Optimizer Demo)"
          />
          <div>
            <input type="file" accept=".md,.txt" onChange={handleFileUpload} aria-label="Upload specification markdown" />
          </div>
          <div style={{ display: 'grid', gridTemplateColumns: '1fr 1fr', gap: 12 }}>
            <Textarea
              rows={14}
              value={specMarkdown}
              onChange={(_, d) => setSpecMarkdown(d.value)}
              style={{ fontFamily: 'monospace' }}
            />
            <div
              style={{
                padding: 14,
                borderRadius: 8,
                background: tokens.colorNeutralBackground2,
                whiteSpace: 'pre-wrap',
                fontSize: 13
              }}
            >
              <Text weight="semibold" block style={{ marginBottom: 6 }}>
                Live Specification Preview
              </Text>
              {specMarkdown}
            </div>
          </div>
          <div style={{ display: 'flex', justifyContent: 'flex-end' }}>
            <Button
              appearance="primary"
              disabled={!name.trim() || !specMarkdown.trim() || intakeMutation.isPending}
              onClick={() => intakeMutation.mutate()}
            >
              Analyze Spec & Run Public-Safe Check →
            </Button>
          </div>
        </Card>
      )}

      {step === 2 && (
        <Card style={{ padding: 20, display: 'flex', flexDirection: 'column', gap: 14 }}>
          <Text size={400} weight="semibold">
            2. PM Agent Clarifying Questions & Public-Repo Hygiene Check
          </Text>
          {publicSafe.passed ? (
            <Badge appearance="tint" color="success">
              Public-Safe Check Passed — No customer names, internal URLs, secrets, or PII detected.
            </Badge>
          ) : (
            <div style={{ padding: 12, borderRadius: 8, background: tokens.colorPaletteRedBackground1 }}>
              <Text weight="semibold" block>
                Public-Safe Check Flagged Items (Repository is public by default):
              </Text>
              {publicSafe.flags.map((f: any, idx: number) => (
                <Text key={idx} size={200} block>
                  • [{f.category}] {f.snippet} — {f.reason}
                </Text>
              ))}
              <Checkbox
                label="Dismiss flagged items as synthetic / intentional public-safe examples"
                checked={dismissFlags}
                onChange={(_, d) => setDismissFlags(Boolean(d.checked))}
              />
            </div>
          )}

          {questions.length === 0 ? (
            <Text>Specification meets the Definition of Ready with no open ambiguities.</Text>
          ) : (
            questions.map((q, i) => (
              <div key={i} style={{ display: 'flex', flexDirection: 'column', gap: 6 }}>
                <Text weight="semibold">{q}</Text>
                <Input
                  placeholder="Your answer (or leave blank to skip)..."
                  value={answers[q] ?? ''}
                  onChange={(_, d) => setAnswers(prev => ({ ...prev, [q]: d.value }))}
                />
              </div>
            ))
          )}
          <div style={{ display: 'flex', justifyContent: 'space-between' }}>
            <Button appearance="secondary" onClick={() => setStep(1)}>
              ← Back
            </Button>
            <Button appearance="primary" onClick={() => setStep(3)}>
              Next: Stack & Autonomy →
            </Button>
          </div>
        </Card>
      )}

      {step === 3 && (
        <Card style={{ padding: 20, display: 'flex', flexDirection: 'column', gap: 14 }}>
          <Text size={400} weight="semibold">
            3. Golden-Path Stack Template, Autonomy & Coding Model
          </Text>
          <div>
            <Text weight="semibold" block style={{ marginBottom: 4 }}>
              Golden-Path Template
            </Text>
            <Dropdown
              value={templateId}
              onOptionSelect={(_, d) => d.optionValue && setTemplateId(d.optionValue)}
            >
              {(templatesQuery.data ?? []).map((t: any) => (
                <Option key={t.id} value={t.id}>
                  {`${t.name} (${t.version})`}
                </Option>
              ))}
            </Dropdown>
          </div>

          <div>
            <Text weight="semibold" block style={{ marginBottom: 4 }}>
              Autonomy Level
            </Text>
            <Dropdown
              value={autonomy}
              onOptionSelect={(_, d) => d.optionValue && setAutonomy(d.optionValue as any)}
            >
              <Option value="L0">L0 Shadow (opens PRs, never auto-merges)</Option>
              <Option value="L1">L1 Supervised (auto-merge except risk labels & AT PRs)</Option>
              <Option value="L2">L2 Dark (unattended; human approval only on escalations/upgrades)</Option>
            </Dropdown>
          </div>

          <div>
            <Text weight="semibold" block style={{ marginBottom: 4 }}>
              Default Coding Model
            </Text>
            <Dropdown
              value={codingModel}
              onOptionSelect={(_, d) => d.optionValue && setCodingModel(d.optionValue)}
            >
              {(modelsQuery.data ?? [])
                .filter((m: any) => m.enabled)
                .map((m: any) => (
                  <Option key={m.id} value={m.apiModelId}>
                    {m.displayName}
                  </Option>
                ))}
            </Dropdown>
          </div>

          <Text size={200} style={{ color: tokens.colorNeutralForeground3 }}>
            Visibility: Public (GitHub security baseline: secret scanning + push protection + CodeQL default setup).
            Deployment target: Azure Container Apps per-PR previews + live demo environment via OIDC.
          </Text>

          <div style={{ display: 'flex', justifyContent: 'space-between' }}>
            <Button appearance="secondary" onClick={() => setStep(2)}>
              ← Back
            </Button>
            <Button
              appearance="primary"
              disabled={clarifyAndPlanMutation.isPending}
              onClick={() => clarifyAndPlanMutation.mutate()}
            >
              Generate Architecture & Backlog Plan →
            </Button>
          </div>
        </Card>
      )}

      {step === 4 && plan && (
        <Card style={{ padding: 20, display: 'flex', flexDirection: 'column', gap: 14 }}>
          <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center', flexWrap: 'wrap', gap: 8 }}>
            <div>
              <Text size={400} weight="semibold" block>
                4. Human Checkpoint — Plan & Dependency Review
              </Text>
              <Text size={200} style={{ color: tokens.colorNeutralForeground3 }}>
                Forecast: ~${plan.estimatedCostUsd} Copilot spend · ~{plan.estimatedDurationMinutes} mins · {plan.items.length} issues
              </Text>
            </div>
            <div style={{ display: 'flex', gap: 8 }}>
              <Button appearance="secondary" onClick={() => savePlanMutation.mutate(plan)}>
                Save Edits
              </Button>
              <Button
                appearance="primary"
                disabled={approveAndLaunchMutation.isPending || plan.items.length === 0}
                onClick={() => approveAndLaunchMutation.mutate()}
              >
                Approve Plan & Bootstrap Repository →
              </Button>
            </div>
          </div>

          <div style={{ display: 'grid', gridTemplateColumns: '1.4fr 1fr', gap: 14 }}>
            <div style={{ display: 'flex', flexDirection: 'column', gap: 8 }}>
              {plan.items.map((item, idx) => {
                const missingCriteria = item.acceptanceCriteria.length === 0;
                const oversized = item.size === 'XLarge';
                return (
                  <div
                    key={item.key}
                    style={{
                      padding: 10,
                      borderRadius: 8,
                      background: tokens.colorNeutralBackground2,
                      display: 'flex',
                      flexDirection: 'column',
                      gap: 6
                    }}
                  >
                    <div style={{ display: 'flex', gap: 8, alignItems: 'center' }}>
                      <Badge appearance="tint" color={item.isAcceptanceTest ? 'important' : 'brand'}>
                        {item.key} {item.isAcceptanceTest ? '(AT)' : ''}
                      </Badge>
                      <Input
                        style={{ flex: 1 }}
                        value={item.title}
                        onChange={(_, d) => updateItem(idx, { title: d.value })}
                      />
                      <Dropdown
                        style={{ minWidth: 95 }}
                        value={item.size}
                        onOptionSelect={(_, d) => d.optionValue && updateItem(idx, { size: d.optionValue as any })}
                      >
                        <Option value="Small">Small</Option>
                        <Option value="Medium">Medium</Option>
                        <Option value="Large">Large</Option>
                        <Option value="XLarge">XLarge</Option>
                      </Dropdown>
                      <Dropdown
                        style={{ minWidth: 100 }}
                        value={item.priority}
                        onOptionSelect={(_, d) => d.optionValue && updateItem(idx, { priority: d.optionValue as any })}
                      >
                        <Option value="Critical">Critical</Option>
                        <Option value="High">High</Option>
                        <Option value="Medium">Medium</Option>
                        <Option value="Low">Low</Option>
                      </Dropdown>
                      <Button size="small" appearance="subtle" onClick={() => deleteItem(idx)}>
                        Remove
                      </Button>
                    </div>
                    {(missingCriteria || oversized) && (
                      <Text size={200} style={{ color: tokens.colorPaletteRedForeground1 }}>
                        {missingCriteria ? '⚠ Missing acceptance criteria. ' : ''}
                        {oversized ? '⚠ Item is XLarge; consider splitting.' : ''}
                      </Text>
                    )}
                    <Text size={200} style={{ color: tokens.colorNeutralForeground3 }}>
                      Requirements: {item.requirementIds.join(', ')} · Blocked by:{' '}
                      {item.blockedByKeys.length > 0 ? item.blockedByKeys.join(', ') : 'None'}
                    </Text>
                  </div>
                );
              })}
            </div>

            <div style={{ height: 440, borderRadius: 8, overflow: 'hidden', border: `1px solid ${tokens.colorNeutralStroke2}` }}>
              <ReactFlow nodes={planGraph.nodes} edges={planGraph.edges} fitView>
                <Background />
              </ReactFlow>
            </div>
          </div>
        </Card>
      )}

      {step === 5 && (
        <Card style={{ padding: 20, display: 'flex', flexDirection: 'column', gap: 12 }}>
          <Text size={500} weight="bold">
            5. Repository Provisioned & Factory Running
          </Text>
          {bootstrapSteps.map((s: any) => (
            <div
              key={s.stepNumber}
              style={{
                padding: '10px 14px',
                borderRadius: 8,
                background: tokens.colorNeutralBackground2,
                display: 'flex',
                justifyContent: 'space-between',
                alignItems: 'center'
              }}
            >
              <Text weight="semibold">
                Step {s.stepNumber}: {s.name}
              </Text>
              <StatusPill status={s.status} />
            </div>
          ))}
          <div style={{ display: 'flex', gap: 10, marginTop: 8 }}>
            <Button appearance="primary" onClick={() => navigate('/board')}>
              Open Project Board
            </Button>
            <Button appearance="secondary" onClick={() => navigate('/')}>
              Return to Overview
            </Button>
          </div>
        </Card>
      )}
    </div>
  );
};
