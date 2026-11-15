import React, { useEffect, useState } from 'react';
import {
  Badge,
  Button,
  Card,
  Divider,
  Dropdown,
  Input,
  Option,
  Text,
  tokens
} from '@fluentui/react-components';
import {
  Key20Regular,
  PersonPasskey20Regular,
  WeatherMoon20Regular,
  WeatherSunny20Regular
} from '@fluentui/react-icons';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { api } from '../../api/client';
import { ThemeMode } from '../../theme/factoryTheme';

export const LoginPage: React.FC<{
  isDark: boolean;
  onToggleTheme: () => void;
  themeMode?: ThemeMode;
}> = ({ isDark, onToggleTheme }) => {
  const qc = useQueryClient();
  const configQuery = useQuery({
    queryKey: ['authConfig'],
    queryFn: api.getAuthConfig
  });

  const cfg = configQuery.data;

  const [showManualGitHubSetup, setShowManualGitHubSetup] = useState(false);
  const [clientId, setClientId] = useState('');
  const [clientSecret, setClientSecret] = useState('');
  const [sandboxLogin, setSandboxLogin] = useState('azureholic');
  const [sandboxRole, setSandboxRole] = useState<'Admin' | 'Operator' | 'Viewer'>('Admin');
  const [errorBanner, setErrorBanner] = useState<string | null>(() => {
    if (typeof window === 'undefined') return null;
    return new URLSearchParams(window.location.search).get('authError');
  });

  useEffect(() => {
    if (cfg?.defaultOwnerLogin) {
      setSandboxLogin(cfg.defaultOwnerLogin);
    }
  }, [cfg?.defaultOwnerLogin]);

  const saveOAuthMutation = useMutation({
    mutationFn: () => api.saveGitHubOAuthConfig({ clientId, clientSecret }),
    onSuccess: () => {
      setErrorBanner(null);
      qc.invalidateQueries({ queryKey: ['authConfig'] });
      window.location.href = '/auth/github/login';
    },
    onError: (err: Error) => setErrorBanner(err.message)
  });

  const sandboxSignInMutation = useMutation({
    mutationFn: () => api.sandboxLogin(sandboxLogin, sandboxRole),
    onSuccess: () => {
      setErrorBanner(null);
      if (window.location.search.includes('authError')) {
        window.history.replaceState({}, '', window.location.pathname);
      }
      qc.invalidateQueries({ queryKey: ['me'] });
    },
    onError: (err: Error) => setErrorBanner(err.message)
  });

  return (
    <div
      style={{
        minHeight: '100vh',
        display: 'flex',
        flexDirection: 'column',
        background: tokens.colorNeutralBackground2
      }}
    >
      <header
        style={{
          height: 54,
          padding: '0 20px',
          display: 'flex',
          alignItems: 'center',
          justifyContent: 'space-between'
        }}
      >
        <div style={{ display: 'flex', alignItems: 'center', gap: 10 }}>
          <img
            src="/logo.png"
            alt="Demo-Factory logo"
            width={28}
            height={28}
            style={{ display: 'block', borderRadius: 6 }}
          />
          <Text weight="bold" size={400}>
            Demo-Factory
          </Text>
        </div>
        <Button
          size="small"
          appearance="subtle"
          aria-label="Toggle light or dark theme"
          icon={isDark ? <WeatherSunny20Regular /> : <WeatherMoon20Regular />}
          onClick={onToggleTheme}
        />
      </header>

      <main
        style={{
          flex: 1,
          display: 'flex',
          alignItems: 'center',
          justifyContent: 'center',
          padding: 24
        }}
      >
        <Card
          style={{
            width: '100%',
            maxWidth: 480,
            padding: 28,
            display: 'flex',
            flexDirection: 'column',
            gap: 18
          }}
        >
          <div>
            <Badge appearance="tint" color="brand" style={{ marginBottom: 8 }}>
              GitHub Software Factory · Session Authentication
            </Badge>
            <Text size={600} weight="bold" block>
              Sign in to Demo-Factory
            </Text>
            <Text size={300} style={{ color: tokens.colorNeutralForeground3, marginTop: 4, display: 'block' }}>
              Authenticate with GitHub so the factory can act on your behalf using an HTTP-only session cookie. GitHub
              tokens never reach the browser.
            </Text>
          </div>

          {errorBanner && (
            <div
              role="alert"
              style={{
                padding: 12,
                borderRadius: 8,
                background: tokens.colorPaletteRedBackground1,
                borderLeft: `4px solid ${tokens.colorPaletteRedBorderActive}`
              }}
            >
              <Text weight="semibold">{errorBanner}</Text>
            </div>
          )}

          {/* GitHub Sign-In Section */}
          <div style={{ display: 'flex', flexDirection: 'column', gap: 10 }}>
            {cfg?.gitHubConfigured ? (
              <>
                <Button
                  size="large"
                  appearance="primary"
                  icon={<PersonPasskey20Regular />}
                  as="a"
                  href="/auth/github/login"
                >
                  Sign in with GitHub
                </Button>
                <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center' }}>
                  <Text size={200} style={{ color: tokens.colorNeutralForeground3 }}>
                    Connected App: <code>{cfg.appSlug}</code> ({cfg.clientId.slice(0, 10)}…)
                  </Text>
                  <Button
                    size="small"
                    appearance="subtle"
                    onClick={() => setShowManualGitHubSetup(v => !v)}
                  >
                    Change OAuth App
                  </Button>
                </div>
              </>
            ) : (
              <div
                style={{
                  padding: 14,
                  borderRadius: 8,
                  background: tokens.colorNeutralBackground3,
                  display: 'flex',
                  flexDirection: 'column',
                  gap: 10
                }}
              >
                <Text weight="semibold">Connect your personal GitHub account</Text>
                <Text size={200} style={{ color: tokens.colorNeutralForeground3 }}>
                  Create a private Demo-Factory GitHub App on your account in one click (pre-configured with repository
                  permissions &amp; localhost callback), or enter an existing GitHub App / OAuth App Client ID &amp; Secret.
                </Text>
                <Button
                  size="large"
                  appearance="primary"
                  icon={<PersonPasskey20Regular />}
                  as="a"
                  href="/auth/github/manifest-start"
                >
                  Create &amp; Sign in with GitHub App (1-Click)
                </Button>
                <Button
                  size="small"
                  appearance="outline"
                  icon={<Key20Regular />}
                  onClick={() => setShowManualGitHubSetup(v => !v)}
                >
                  {showManualGitHubSetup
                    ? 'Hide existing OAuth App setup'
                    : 'Already have a GitHub App or OAuth App? Enter Client ID'}
                </Button>
              </div>
            )}

            {showManualGitHubSetup && (
              <div
                style={{
                  padding: 14,
                  borderRadius: 8,
                  border: `1px solid ${tokens.colorNeutralStroke2}`,
                  display: 'flex',
                  flexDirection: 'column',
                  gap: 10
                }}
              >
                <Text size={200} style={{ color: tokens.colorNeutralForeground3 }}>
                  Authorization callback URL:{' '}
                  <code>{cfg?.callbackUrl ?? 'http://localhost:5173/auth/github/callback'}</code>
                </Text>
                <Input
                  placeholder="GitHub App / OAuth Client ID (e.g. Iv23li...)"
                  value={clientId}
                  onChange={(_, d) => setClientId(d.value)}
                />
                <Input
                  type="password"
                  placeholder="GitHub Client Secret"
                  value={clientSecret}
                  onChange={(_, d) => setClientSecret(d.value)}
                />
                <Button
                  appearance="primary"
                  disabled={!clientId.trim() || !clientSecret.trim() || saveOAuthMutation.isPending}
                  onClick={() => saveOAuthMutation.mutate()}
                >
                  Save Credentials &amp; Sign in with GitHub →
                </Button>
              </div>
            )}
          </div>

          {/* Local Sandbox Sign-In Section (Development / Offline Testing) */}
          {cfg?.sandboxAvailable && (
            <>
              <Divider>Local Development Sandbox</Divider>
              <div style={{ display: 'flex', flexDirection: 'column', gap: 10 }}>
                <Text size={200} style={{ color: tokens.colorNeutralForeground3 }}>
                  Or sign in locally with a real HTTP-only session cookie using a sandbox persona &amp; role:
                </Text>
                <div style={{ display: 'grid', gridTemplateColumns: '1.3fr 1fr', gap: 8 }}>
                  <Input
                    aria-label="Sandbox GitHub username"
                    placeholder="GitHub login"
                    value={sandboxLogin}
                    onChange={(_, d) => setSandboxLogin(d.value)}
                  />
                  <Dropdown
                    aria-label="Sandbox RBAC role"
                    value={sandboxRole}
                    onOptionSelect={(_, d) => d.optionValue && setSandboxRole(d.optionValue as any)}
                  >
                    <Option value="Admin">Admin</Option>
                    <Option value="Operator">Operator</Option>
                    <Option value="Viewer">Viewer</Option>
                  </Dropdown>
                </div>
                <Button
                  appearance="secondary"
                  disabled={!sandboxLogin.trim() || sandboxSignInMutation.isPending}
                  onClick={() => sandboxSignInMutation.mutate()}
                >
                  Sign in as @{sandboxLogin || 'azureholic'} ({sandboxRole})
                </Button>
              </div>
            </>
          )}
        </Card>
      </main>
    </div>
  );
};
