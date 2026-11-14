import {
  BrandVariants,
  Theme,
  createDarkTheme,
  createLightTheme,
  teamsHighContrastTheme
} from '@fluentui/react-components';
import { useEffect, useMemo, useState } from 'react';

export type ThemeMode = 'system' | 'light' | 'dark';
export type DensityMode = 'comfortable' | 'compact';

export const demoFactoryBrand: BrandVariants = {
  10: '#041224',
  20: '#0a203e',
  30: '#102e59',
  40: '#153d76',
  50: '#1a4d94',
  60: '#1f5eb3',
  70: '#2570d3',
  80: '#2b82f0',
  90: '#4b95f4',
  100: '#6ba8f7',
  110: '#8bbbfa',
  120: '#abcdfc',
  130: '#cbe0fe',
  140: '#e3eeff',
  150: '#f1f7ff',
  160: '#fafcff'
};

export const factoryLightTheme: Theme = createLightTheme(demoFactoryBrand);
export const factoryDarkTheme: Theme = {
  ...createDarkTheme(demoFactoryBrand),
  colorNeutralBackground1: '#11151c',
  colorNeutralBackground2: '#171c26',
  colorNeutralBackground3: '#1e2431'
};

export function useResolvedFluentTheme(mode: ThemeMode) {
  const [prefersDark, setPrefersDark] = useState<boolean>(() =>
    typeof window !== 'undefined' && window.matchMedia?.('(prefers-color-scheme: dark)').matches
  );
  const [forcedColors, setForcedColors] = useState<boolean>(() =>
    typeof window !== 'undefined' && window.matchMedia?.('(forced-colors: active)').matches
  );

  useEffect(() => {
    if (typeof window === 'undefined' || !window.matchMedia) return;
    const darkMq = window.matchMedia('(prefers-color-scheme: dark)');
    const hcMq = window.matchMedia('(forced-colors: active)');
    const onDark = (e: MediaQueryListEvent) => setPrefersDark(e.matches);
    const onHc = (e: MediaQueryListEvent) => setForcedColors(e.matches);
    darkMq.addEventListener('change', onDark);
    hcMq.addEventListener('change', onHc);
    return () => {
      darkMq.removeEventListener('change', onDark);
      hcMq.removeEventListener('change', onHc);
    };
  }, []);

  const isDark = mode === 'dark' || (mode === 'system' && prefersDark);

  const theme = useMemo<Theme>(() => {
    if (forcedColors) return teamsHighContrastTheme;
    return isDark ? factoryDarkTheme : factoryLightTheme;
  }, [forcedColors, isDark]);

  return { theme, isDark, forcedColors };
}
