import { describe, expect, it } from 'vitest';
import { demoFactoryBrand, factoryDarkTheme, factoryLightTheme } from '../theme/factoryTheme';

describe('Demo-Factory Fluent 2 theme', () => {
  it('generates distinct light and dark themes from brand ramp', () => {
    expect(demoFactoryBrand[80]).toBe('#2b82f0');
    expect(factoryLightTheme.colorNeutralBackground1).not.toBe(factoryDarkTheme.colorNeutralBackground1);
  });
});
