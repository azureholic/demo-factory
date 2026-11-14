import React, { useEffect, useState } from 'react';
import {
  Avatar,
  Badge,
  Button,
  Dialog,
  DialogBody,
  DialogContent,
  DialogSurface,
  DialogTitle,
  Dropdown,
  FluentProvider,
  Input,
  Option,
  Text,
  tokens
} from '@fluentui/react-components';
import {
  Board20Regular,
  Bot20Regular,
  BranchFork20Regular,
  AddCircle20Regular,
  DocumentText20Regular,
  Globe20Regular,
  History20Regular,
  Home20Regular,
  MailInbox20Regular,
  Navigation20Regular,
  PauseCircle20Regular,
  PlayCircle20Regular,
  Search20Regular,
  Settings20Regular,
  ShieldCheckmark20Regular,
  Sparkle20Regular,
  WeatherMoon20Regular,
  WeatherSunny20Regular
} from '@fluentui/react-icons';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { Route, Routes, useLocation, useNavigate } from 'react-router-dom';
import { api } from '../api/client';
import { AndonBanner, StatusPill } from '../components/SharedComponents';
import { AuditPage } from '../features/audit/AuditPage';
import { BoardPage } from '../features/board/BoardPage';
import { FeaturesPage } from '../features/features/FeaturesPage';
import { GraphPage } from '../features/graph/GraphPage';
import { InboxPage } from '../features/inbox/InboxPage';
import { OverviewPage } from '../features/overview/OverviewPage';
import { PoliciesPage } from '../features/policies/PoliciesPage';
import { PreviewsPage } from '../features/previews/PreviewsPage';
import { NewProjectWizardPage } from '../features/projects/NewProjectWizardPage';
import { ReposPage } from '../features/repos/ReposPage';
import { SessionsPage } from '../features/sessions/SessionsPage';
import { SettingsPage } from '../features/settings/SettingsPage';
import { SpecPage } from '../features/spec/SpecPage';
import { WorkItemDetailPage } from '../features/workitem/WorkItemDetailPage';
import { useRealtime } from '../realtime/SignalRProvider';
import { DensityMode, ThemeMode, useResolvedFluentTheme } from '../theme/factoryTheme';

interface NavEntry {
  path: string;
  label: string;
  icon: React.ReactNode;
}

const NAV_ITEMS: NavEntry[] = [
  { path: '/', label: 'Overview', icon: <Home20Regular /> },
  { path: '/projects/new', label: 'New Project', icon: <AddCircle20Regular /> },
  { path: '/board', label: 'Board', icon: <Board20Regular /> },
  { path: '/graph', label: 'Dependency Graph', icon: <BranchFork20Regular /> },
  { path: '/features', label: 'Features', icon: <Sparkle20Regular /> },
  { path: '/inbox', label: 'Issue Inbox', icon: <MailInbox20Regular /> },
  { path: '/spec', label: 'Living Spec', icon: <DocumentText20Regular /> },
  { path: '/sessions', label: 'Sessions', icon: <Bot20Regular /> },
  { path: '/previews', label: 'Previews & Demo', icon: <Globe20Regular /> },
  { path: '/repos', label: 'Repositories', icon: <ShieldCheckmark20Regular /> },
  { path: '/policies', label: 'Policies & Models', icon: <ShieldCheckmark20Regular /> },
  { path: '/audit', label: 'Audit Log', icon: <History20Regular /> },
  { path: '/settings', label: 'Settings', icon: <Settings20Regular /> }
];

export const AppShell: React.FC = () => {
  const navigate = useNavigate();
  const location = useLocation();
  const qc = useQueryClient();
  const { connected } = useRealtime();

  const [themeMode, setThemeMode] = useState<ThemeMode>(() =>
    (localStorage.getItem('df_theme') as ThemeMode) || 'system'
  );
  const [density, setDensity] = useState<DensityMode>(() =>
    (localStorage.getItem('df_density') as DensityMode) || 'comfortable'
  );
  const [navCollapsed, setNavCollapsed] = useState(false);
  const [searchOpen, setSearchOpen] = useState(false);
  const [searchQuery, setSearchQuery] = useState('');
  const [selectedRepoId, setSelectedRepoId] = useState<string>('');

  const { theme, isDark } = useResolvedFluentTheme(themeMode);

  useEffect(() => {
    document.documentElement.setAttribute('data-density', density);
  }, [density]);

  // Ctrl+K Global Search shortcut (§14.1)
  useEffect(() => {
    const onKeyDown = (e: KeyboardEvent) => {
      if ((e.ctrlKey || e.metaKey) && e.key.toLowerCase() === 'k') {
        e.preventDefault();
        setSearchOpen(prev => !prev);
      }
    };
    window.addEventListener('keydown', onKeyDown);
    return () => window.removeEventListener('keydown', onKeyDown);
  }, []);

  const meQuery = useQuery({ queryKey: ['me'], queryFn: api.getMe });
  const statusQuery = useQuery({ queryKey: ['factoryStatus'], queryFn: api.getFactoryStatus });
  const reposQuery = useQuery({ queryKey: ['repos'], queryFn: api.getRepos });
  const projectsQuery = useQuery({ queryKey: ['projects'], queryFn: api.getProjects });

  const repos = reposQuery.data ?? [];
  const projects = projectsQuery.data ?? [];

  useEffect(() => {
    if (!selectedRepoId && repos.length > 0) {
      setSelectedRepoId(repos[0].id);
    }
  }, [repos, selectedRepoId]);

  const activeRepo = repos.find(r => r.id === selectedRepoId) ?? repos[0];
  const activeProject = projects.find(p => p.id === activeRepo?.projectId) ?? projects[0];

  const boardForSearch = useQuery({
    queryKey: ['board', activeRepo?.id, ''],
    queryFn: () => api.getBoard(activeRepo!.id),
    enabled: Boolean(activeRepo?.id && searchOpen)
  });

  const killSwitchMutation = useMutation({
    mutationFn: () =>
      statusQuery.data?.globalPaused ? api.resumeFactory() : api.pauseFactory(),
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: ['factoryStatus'] });
      qc.invalidateQueries({ queryKey: ['repos'] });
    }
  });

  const handleThemeChange = (mode: ThemeMode) => {
    setThemeMode(mode);
    localStorage.setItem('df_theme', mode);
    api.savePreferences({ themePreference: mode }).catch(() => undefined);
  };

  const handleDensityChange = (d: DensityMode) => {
    setDensity(d);
    localStorage.setItem('df_density', d);
    api.savePreferences({ densityPreference: d }).catch(() => undefined);
  };

  const stoppedRepos = repos.filter(r => r.lineStatus === 'LineStopped');
  const factoryStatus = statusQuery.data?.status ?? 'Running';

  const matchingNav = NAV_ITEMS.filter(n =>
    n.label.toLowerCase().includes(searchQuery.toLowerCase())
  );
  const matchingCards = (boardForSearch.data?.cards ?? []).filter(
    c =>
      c.title.toLowerCase().includes(searchQuery.toLowerCase()) ||
      String(c.issueNumber).includes(searchQuery)
  );

  return (
    <FluentProvider theme={theme} style={{ minHeight: '100vh', display: 'flex', flexDirection: 'column' }}>
      <AndonBanner
        stoppedRepos={stoppedRepos}
        reconnectRequired={Boolean(statusQuery.data?.reconnectRequired)}
      />

      {/* Top App Bar (§14.1) */}
      <header
        style={{
          height: 54,
          padding: '0 16px',
          borderBottom: `1px solid ${tokens.colorNeutralStroke2}`,
          background: tokens.colorNeutralBackground2,
          display: 'flex',
          alignItems: 'center',
          justifyContent: 'space-between',
          gap: 12
        }}
      >
        <div style={{ display: 'flex', alignItems: 'center', gap: 12 }}>
          <Button
            appearance="subtle"
            icon={<Navigation20Regular />}
            aria-label="Toggle navigation drawer"
            onClick={() => setNavCollapsed(v => !v)}
          />
          <Text
            size={400}
            weight="bold"
            style={{ cursor: 'pointer' }}
            onClick={() => navigate('/')}
          >
            Demo-Factory
          </Text>
          <StatusPill status={factoryStatus} />
          <Badge appearance="outline" color={connected ? 'success' : 'informative'}>
            {connected ? 'Live' : 'Polling'}
          </Badge>

          {repos.length > 0 && (
            <Dropdown
              aria-label="Switch repository"
              value={activeRepo?.fullName ?? ''}
              onOptionSelect={(_, d) => d.optionValue && setSelectedRepoId(d.optionValue)}
            >
              {repos.map(r => (
                <Option key={r.id} value={r.id}>
                  {r.fullName}
                </Option>
              ))}
            </Dropdown>
          )}
        </div>

        <div style={{ display: 'flex', alignItems: 'center', gap: 8 }}>
          <Button
            size="small"
            appearance="outline"
            icon={<Search20Regular />}
            onClick={() => setSearchOpen(true)}
          >
            Search (Ctrl+K)
          </Button>

          <Button
            size="small"
            appearance="subtle"
            icon={statusQuery.data?.globalPaused ? <PlayCircle20Regular /> : <PauseCircle20Regular />}
            onClick={() => killSwitchMutation.mutate()}
          >
            {statusQuery.data?.globalPaused ? 'Resume Factory' : 'Pause'}
          </Button>

          <Button
            size="small"
            appearance="subtle"
            aria-label="Toggle light or dark theme"
            icon={isDark ? <WeatherSunny20Regular /> : <WeatherMoon20Regular />}
            onClick={() => handleThemeChange(isDark ? 'light' : 'dark')}
          />

          <Avatar name={meQuery.data?.login ?? 'Operator'} size={28} color="brand" />
        </div>
      </header>

      {/* Collapsible NavDrawer + Main Content */}
      <div style={{ display: 'flex', flex: 1, minHeight: 0 }}>
        <nav
          aria-label="Primary Factory Navigation"
          style={{
            width: navCollapsed ? 60 : 225,
            transition: 'width 0.15s ease',
            borderRight: `1px solid ${tokens.colorNeutralStroke2}`,
            background: tokens.colorNeutralBackground2,
            padding: 8,
            display: 'flex',
            flexDirection: 'column',
            gap: 4
          }}
        >
          {NAV_ITEMS.map(item => {
            const active =
              item.path === '/'
                ? location.pathname === '/'
                : location.pathname.startsWith(item.path);
            return (
              <Button
                key={item.path}
                appearance={active ? 'secondary' : 'subtle'}
                icon={item.icon as any}
                style={{
                  justifyContent: navCollapsed ? 'center' : 'flex-start',
                  fontWeight: active ? 600 : 400
                }}
                title={item.label}
                onClick={() => navigate(item.path)}
              >
                {!navCollapsed ? item.label : undefined}
              </Button>
            );
          })}
        </nav>

        <main style={{ flex: 1, padding: 20, overflowY: 'auto' }}>
          <Routes>
            <Route path="/" element={<OverviewPage selectedRepoId={activeRepo?.id} />} />
            <Route path="/projects/new" element={<NewProjectWizardPage />} />
            <Route
              path="/board"
              element={<BoardPage repoId={activeRepo?.id} projectId={activeProject?.id} />}
            />
            <Route path="/graph" element={<GraphPage repoId={activeRepo?.id} />} />
            <Route
              path="/features"
              element={<FeaturesPage projectId={activeProject?.id} repoId={activeRepo?.id} />}
            />
            <Route path="/inbox" element={<InboxPage projectId={activeProject?.id} />} />
            <Route path="/spec" element={<SpecPage projectId={activeProject?.id} />} />
            <Route path="/workitems/:id" element={<WorkItemDetailPage />} />
            <Route path="/sessions" element={<SessionsPage repoId={activeRepo?.id} />} />
            <Route
              path="/previews"
              element={<PreviewsPage project={activeProject} repoId={activeRepo?.id} />}
            />
            <Route path="/repos" element={<ReposPage selectedRepoId={activeRepo?.id} />} />
            <Route path="/policies" element={<PoliciesPage repoId={activeRepo?.id} />} />
            <Route path="/audit" element={<AuditPage />} />
            <Route
              path="/settings"
              element={
                <SettingsPage
                  themeMode={themeMode}
                  onThemeChange={handleThemeChange}
                  density={density}
                  onDensityChange={handleDensityChange}
                />
              }
            />
          </Routes>
        </main>
      </div>

      {/* Ctrl+K Global Command & Work-Item Palette */}
      <Dialog open={searchOpen} onOpenChange={(_, d) => setSearchOpen(d.open)}>
        <DialogSurface>
          <DialogBody>
            <DialogTitle>Quick Navigation & Work-Item Search</DialogTitle>
            <DialogContent style={{ display: 'flex', flexDirection: 'column', gap: 10 }}>
              <Input
                autoFocus
                placeholder="Search screens or #issue title..."
                value={searchQuery}
                onChange={(_, d) => setSearchQuery(d.value)}
              />
              <div style={{ display: 'flex', flexDirection: 'column', gap: 4, maxHeight: 280, overflowY: 'auto' }}>
                {matchingNav.map(n => (
                  <Button
                    key={n.path}
                    appearance="subtle"
                    style={{ justifyContent: 'flex-start' }}
                    onClick={() => {
                      navigate(n.path);
                      setSearchOpen(false);
                    }}
                  >
                    Go to {n.label}
                  </Button>
                ))}
                {matchingCards.map(c => (
                  <Button
                    key={c.id}
                    appearance="subtle"
                    style={{ justifyContent: 'flex-start' }}
                    onClick={() => {
                      navigate(`/workitems/${c.id}`);
                      setSearchOpen(false);
                    }}
                  >
                    #{c.issueNumber} {c.title} ({c.boardStatus})
                  </Button>
                ))}
              </div>
            </DialogContent>
          </DialogBody>
        </DialogSurface>
      </Dialog>
    </FluentProvider>
  );
};
