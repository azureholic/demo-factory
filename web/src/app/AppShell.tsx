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
  Divider,
  Dropdown,
  FluentProvider,
  Input,
  Menu,
  MenuDivider,
  MenuItem,
  MenuList,
  MenuPopover,
  MenuTrigger,
  Option,
  Popover,
  PopoverSurface,
  PopoverTrigger,
  Text,
  Tooltip,
  tokens
} from '@fluentui/react-components';
import {
  Board20Regular,
  Bot20Regular,
  BranchFork20Regular,
  AddCircle20Regular,
  ChevronDown16Regular,
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
  SignOut20Regular,
  Sparkle20Regular,
  WeatherMoon20Regular,
  WeatherSunny20Regular
} from '@fluentui/react-icons';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { Route, Routes, useLocation, useNavigate } from 'react-router-dom';
import { CurrentUser, api } from '../api/client';
import { AndonBanner, PageSkeleton, StatusPill } from '../components/SharedComponents';
import { AuditPage } from '../features/audit/AuditPage';
import { LoginPage } from '../features/auth/LoginPage';
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
import { SignalRProvider, useRealtime } from '../realtime/SignalRProvider';
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
  const [themeMode, setThemeMode] = useState<ThemeMode>(() =>
    (localStorage.getItem('df_theme') as ThemeMode) || 'system'
  );
  const [density, setDensity] = useState<DensityMode>(() =>
    (localStorage.getItem('df_density') as DensityMode) || 'comfortable'
  );

  const { theme, isDark } = useResolvedFluentTheme(themeMode);

  useEffect(() => {
    document.documentElement.setAttribute('data-density', density);
  }, [density]);

  const meQuery = useQuery({
    queryKey: ['me'],
    queryFn: api.getMe,
    retry: false
  });

  const handleThemeChange = (mode: ThemeMode) => {
    setThemeMode(mode);
    localStorage.setItem('df_theme', mode);
    if (meQuery.data) {
      api.savePreferences({ themePreference: mode }).catch(() => undefined);
    }
  };

  const handleDensityChange = (d: DensityMode) => {
    setDensity(d);
    localStorage.setItem('df_density', d);
    if (meQuery.data) {
      api.savePreferences({ densityPreference: d }).catch(() => undefined);
    }
  };

  if (meQuery.isLoading) {
    return (
      <FluentProvider theme={theme} style={{ minHeight: '100vh', padding: 32 }}>
        <PageSkeleton cards={4} />
      </FluentProvider>
    );
  }

  if (!meQuery.data) {
    return (
      <FluentProvider theme={theme} style={{ minHeight: '100vh' }}>
        <LoginPage
          isDark={isDark}
          themeMode={themeMode}
          onToggleTheme={() => handleThemeChange(isDark ? 'light' : 'dark')}
        />
      </FluentProvider>
    );
  }

  return (
    <FluentProvider theme={theme} style={{ minHeight: '100vh', display: 'flex', flexDirection: 'column' }}>
      <SignalRProvider>
        <AuthenticatedWorkspace
          me={meQuery.data}
          isDark={isDark}
          themeMode={themeMode}
          density={density}
          onThemeChange={handleThemeChange}
          onDensityChange={handleDensityChange}
        />
      </SignalRProvider>
    </FluentProvider>
  );
};

const AuthenticatedWorkspace: React.FC<{
  me: CurrentUser;
  isDark: boolean;
  themeMode: ThemeMode;
  density: DensityMode;
  onThemeChange: (m: ThemeMode) => void;
  onDensityChange: (d: DensityMode) => void;
}> = ({ me, isDark, themeMode, density, onThemeChange, onDensityChange }) => {
  const navigate = useNavigate();
  const location = useLocation();
  const qc = useQueryClient();
  const { connected } = useRealtime();

  const [navCollapsed, setNavCollapsed] = useState(false);
  const [searchOpen, setSearchOpen] = useState(false);
  const [statusMenuOpen, setStatusMenuOpen] = useState(false);
  const [searchQuery, setSearchQuery] = useState('');
  const [selectedRepoId, setSelectedRepoId] = useState<string>('');

  // Ctrl+K Global Search shortcut
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

  const logoutMutation = useMutation({
    mutationFn: api.logout,
    onSuccess: () => {
      qc.setQueryData(['me'], null);
      qc.removeQueries({ predicate: q => q.queryKey[0] !== 'me' && q.queryKey[0] !== 'authConfig' });
    }
  });

  const stoppedRepos = repos.filter(r => r.lineStatus === 'LineStopped');
  const factoryStatus = statusQuery.data?.status ?? 'Running';
  const isLiveGitHub = statusQuery.data?.sandboxMode === false;

  const statusDotColor =
    factoryStatus === 'Degraded'
      ? '#ef4444'
      : factoryStatus === 'Paused' || !connected || !isLiveGitHub
      ? '#f59e0b'
      : '#10b981';

  const pipelineTooltipText =
    factoryStatus === 'Paused'
      ? 'Global pause is active (or GitHub OAuth reconnection is needed). No new issues are dispatched to Copilot and auto-merge is held.'
      : factoryStatus === 'Degraded'
      ? 'Stop-the-Line is active on at least one repository because a main-branch check failed. Automated recovery is running.'
      : 'Autonomous scheduler and 30-second PR reconciler are actively dispatching work items and evaluating review gates.';

  const streamTooltipText = connected
    ? 'Connected via SignalR WebSocket (/hubs/factory). Board cards, sessions, approvals, and gate checks push in real time.'
    : 'WebSocket stream is currently reconnecting; the UI is automatically refreshing state via HTTP polling.';

  const modeTooltipText = isLiveGitHub
    ? 'Connected to live GitHub repositories, webhooks, branch rulesets, Actions workflows, and copilot-swe-agent[bot]. Click to open readiness & policies.'
    : 'Running in local simulated Sandbox Mode with synthetic GitHub responses and zero external repository writes. Click to configure Live GitHub mode.';

  const matchingNav = NAV_ITEMS.filter(n =>
    n.label.toLowerCase().includes(searchQuery.toLowerCase())
  );
  const matchingCards = (boardForSearch.data?.cards ?? []).filter(
    c =>
      c.title.toLowerCase().includes(searchQuery.toLowerCase()) ||
      String(c.issueNumber).includes(searchQuery)
  );

  return (
    <>
      <AndonBanner
        stoppedRepos={stoppedRepos}
        reconnectRequired={Boolean(statusQuery.data?.reconnectRequired)}
      />

      {/* Top App Bar */}
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
          <div
            role="button"
            tabIndex={0}
            aria-label="Go to Factory Overview"
            onClick={() => navigate('/')}
            onKeyDown={e => {
              if (e.key === 'Enter' || e.key === ' ') {
                e.preventDefault();
                navigate('/');
              }
            }}
            style={{
              display: 'flex',
              alignItems: 'center',
              gap: 10,
              cursor: 'pointer',
              userSelect: 'none'
            }}
          >
            <img
              src="/logo.png"
              alt="Demo-Factory logo"
              width={30}
              height={30}
              style={{ display: 'block', borderRadius: 7 }}
            />
            <Text size={400} weight="bold">
              Demo-Factory
            </Text>
          </div>

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

          <Popover
            open={statusMenuOpen}
            onOpenChange={(_, data) => setStatusMenuOpen(data.open)}
            positioning="below-end"
          >
            <PopoverTrigger disableButtonEnhancement>
              <Button
                size="small"
                appearance="outline"
                aria-label="System status details"
                iconPosition="after"
                icon={<ChevronDown16Regular />}
              >
                <span style={{ display: 'inline-flex', alignItems: 'center', gap: 7 }}>
                  <span
                    aria-hidden="true"
                    style={{
                      width: 8,
                      height: 8,
                      borderRadius: '50%',
                      backgroundColor: statusDotColor,
                      boxShadow: `0 0 0 2px ${statusDotColor}33`
                    }}
                  />
                  <span>Status: {factoryStatus}</span>
                </span>
              </Button>
            </PopoverTrigger>
            <PopoverSurface style={{ width: 310, padding: 14 }}>
              <div style={{ display: 'flex', flexDirection: 'column', gap: 10 }}>
                <div>
                  <Text size={300} weight="semibold" block>
                    System &amp; Connection Status
                  </Text>
                  <Text size={100} style={{ color: tokens.colorNeutralForeground3 }}>
                    Hover over any status pill for an explanation
                  </Text>
                </div>
                <Divider />

                <div style={{ display: 'flex', alignItems: 'center', justifyContent: 'space-between', gap: 12 }}>
                  <Text size={200} weight="medium">
                    Factory Pipeline
                  </Text>
                  <Tooltip content={pipelineTooltipText} relationship="description" withArrow positioning="before">
                    <span tabIndex={0} style={{ display: 'inline-flex', cursor: 'help' }}>
                      <StatusPill status={factoryStatus} />
                    </span>
                  </Tooltip>
                </div>

                <div style={{ display: 'flex', alignItems: 'center', justifyContent: 'space-between', gap: 12 }}>
                  <Text size={200} weight="medium">
                    Realtime Updates
                  </Text>
                  <Tooltip content={streamTooltipText} relationship="description" withArrow positioning="before">
                    <span tabIndex={0} style={{ display: 'inline-flex', cursor: 'help' }}>
                      <Badge appearance="outline" color={connected ? 'success' : 'informative'}>
                        {connected ? 'Live' : 'Polling'}
                      </Badge>
                    </span>
                  </Tooltip>
                </div>

                <div style={{ display: 'flex', alignItems: 'center', justifyContent: 'space-between', gap: 12 }}>
                  <Text size={200} weight="medium">
                    Execution Mode
                  </Text>
                  <Tooltip content={modeTooltipText} relationship="description" withArrow positioning="before">
                    <span
                      role="button"
                      tabIndex={0}
                      onClick={() => {
                        setStatusMenuOpen(false);
                        navigate('/policies');
                      }}
                      onKeyDown={e => {
                        if (e.key === 'Enter' || e.key === ' ') {
                          e.preventDefault();
                          setStatusMenuOpen(false);
                          navigate('/policies');
                        }
                      }}
                      style={{ display: 'inline-flex', cursor: 'pointer' }}
                    >
                      <Badge appearance="tint" color={isLiveGitHub ? 'brand' : 'warning'}>
                        {isLiveGitHub ? 'Live GitHub' : 'Sandbox Mode'}
                      </Badge>
                    </span>
                  </Tooltip>
                </div>
              </div>
            </PopoverSurface>
          </Popover>

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
            onClick={() => onThemeChange(isDark ? 'light' : 'dark')}
          />

          <Menu>
            <MenuTrigger disableButtonEnhancement>
              <Button appearance="subtle" style={{ padding: '2px 8px', gap: 8 }} aria-label="Account menu">
                <Avatar
                  name={me.login}
                  image={me.avatarUrl ? { src: me.avatarUrl } : undefined}
                  size={28}
                  color="brand"
                />
                <Text size={200} weight="semibold">
                  @{me.login}
                </Text>
              </Button>
            </MenuTrigger>
            <MenuPopover>
              <MenuList>
                <div style={{ padding: '6px 12px' }}>
                  <Text weight="semibold" block>
                    @{me.login}
                  </Text>
                  <Text size={200} style={{ color: tokens.colorNeutralForeground3 }}>
                    Role: {me.role} · Auth: {me.authMode ?? 'Session'}
                  </Text>
                </div>
                <MenuDivider />
                <MenuItem onClick={() => navigate('/policies')}>GitHub Connection &amp; Policies</MenuItem>
                <MenuItem icon={<SignOut20Regular />} onClick={() => logoutMutation.mutate()}>
                  Sign out
                </MenuItem>
              </MenuList>
            </MenuPopover>
          </Menu>
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
                  onThemeChange={onThemeChange}
                  density={density}
                  onDensityChange={onDensityChange}
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
    </>
  );
};
