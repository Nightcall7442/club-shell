/**
 * «Создать аккаунт» on the lock screen (D-92), only where the owner allows it (`shell.club.selfSignup`): the player's
 * name, the gender (D-104: required, two big toggles, none picked at first), the date of birth (D-107: required, day,
 * month and year pickers), a login or a phone number to sign in with
 * later, the password twice, and the club rules accepted when the club has any. On success the player is signed in, exactly as after a password login (the lock screen goes on to the
 * tariff picker). Every refusal of the server gets its own line on the field it is about.
 */
import { useMemo, useState, type FormEvent } from 'react';
import clsx from 'clsx';
import { useTranslation } from 'react-i18next';
import type { ClubRules, Locale } from '@clubshell/contracts';
import { BirthDatePicker, EMPTY_BIRTH_DATE, birthDateOf, type BirthDateParts } from '@/components/BirthDatePicker';
import { Button } from '@/components/ui/Button';
import { Input } from '@/components/ui/Input';
import { clubRules } from '@/hooks/useClub';
import { useLocale } from '@/hooks/useLocale';
import { birthDateProblem, birthDateProblemOf, isoDay } from '@/lib/birthDate';
import { errorDetails, errorReason } from '@/lib/errorText';
import { toShellApiError } from '@/lib/tauri';
import { useAuthStore } from '@/store/auth';
import { describeError, useNotificationsStore } from '@/store/notifications';
import { useSettingsStore } from '@/store/settings';

type Field =
  | 'displayName'
  | 'gender'
  | 'birthDate'
  | 'username'
  | 'password'
  | 'passwordAgain'
  | 'acceptRules'
  | 'form';
type Gender = 'male' | 'female';
const GENDERS: readonly Gender[] = ['male', 'female'];
type Errors = Partial<Record<Field, string>>;

/** As the server: a login of 3–32 characters, a password of 4–64. */
const LOGIN_MIN = 3;
const LOGIN_MAX = 32;
const PASSWORD_MIN = 4;
const PASSWORD_MAX = 64;
const NAME_MAX = 64;

/** The rules the player accepts: in their language, else in any the owner wrote (the server asks if any exists). */
function rulesToAccept(rules: ClubRules | null | undefined, locale: Locale): string[] | null {
  return clubRules(rules, locale) ?? clubRules(rules, 'uz') ?? clubRules(rules, 'en');
}

export interface RegisterFormProps {
  /** Back to the sign-in methods. */
  onCancel: () => void;
  className?: string;
}

export function RegisterForm({ onCancel, className }: RegisterFormProps): JSX.Element {
  const { t } = useTranslation();
  const { locale } = useLocale();
  const register = useAuthStore((s) => s.register);
  const push = useNotificationsStore((s) => s.push);
  const clubRulesText = useSettingsStore((s) => s.settings.club?.rules ?? null);
  const rules = useMemo(() => rulesToAccept(clubRulesText, locale), [clubRulesText, locale]);
  const [displayName, setDisplayName] = useState('');
  const [gender, setGender] = useState<Gender | null>(null);
  const [birth, setBirth] = useState<BirthDateParts>(EMPTY_BIRTH_DATE);
  const [username, setUsername] = useState('');
  const [password, setPassword] = useState('');
  const [again, setAgain] = useState('');
  const [accepted, setAccepted] = useState(false);
  const [busy, setBusy] = useState(false);
  const [errors, setErrors] = useState<Errors>({});

  /** The form's own checks, the server's rules in the player's words. */
  const check = (): Errors => {
    const next: Errors = {};
    const name = displayName.trim();
    const login = username.trim();
    if (!name) next.displayName = t('lock.register.nameRequired');
    else if (name.length > NAME_MAX) next.displayName = t('lock.register.nameTooLong');
    if (!gender) next.gender = t('lock.register.genderRequired');
    // D-107: the date of birth is required too.
    const birthProblem = birthDateProblem(birthDateOf(birth), isoDay(new Date()));
    if (birthProblem) next.birthDate = t(`birthDate.problem.${birthProblem}`);
    if (!login) next.username = t('lock.register.loginRequired');
    else if (login.length < LOGIN_MIN || login.length > LOGIN_MAX) next.username = t('lock.register.loginTooShort');
    if (password.length < PASSWORD_MIN) next.password = t('lock.register.passwordTooShort');
    else if (password.length > PASSWORD_MAX) next.password = t('lock.register.passwordTooLong');
    else if (again !== password) next.passwordAgain = t('lock.register.passwordMismatch');
    if (rules && !accepted) next.acceptRules = t('lock.register.rulesRequired');
    return next;
  };

  /** A refusal of `auth.register` on the field it is about. */
  const refusal = (e: unknown): Errors => {
    const err = toShellApiError(e);
    const reason = errorReason(err.details);
    const field = errorDetails(err.details)['field'];
    switch (err.code) {
      case 'validation':
        if (field === 'username') {
          return { username: reason === 'taken' ? t('lock.register.loginTaken') : t('lock.register.loginTooShort') };
        }
        if (field === 'password') return { password: t('lock.register.passwordTooShort') };
        if (field === 'displayName') return { displayName: t('lock.register.nameRequired') };
        if (field === 'gender') return { gender: t('lock.register.genderRequired') };
        if (field === 'birthDate') return { birthDate: t(`birthDate.problem.${birthDateProblemOf(reason)}`) };
        if (field === 'acceptRules') return { acceptRules: t('lock.register.rulesRequired') };
        break;
      case 'forbidden':
        if (reason === 'selfSignupDisabled') return { form: t('lock.register.disabled') };
        // D-108: a minor registering in the forbidden hours.
        if (reason === 'ageRestricted') return { form: describeError(e, 'login') };
        break;
      case 'rateLimited':
        return { form: t('lock.register.tooMany') };
      case 'agentOffline':
      case 'serverUnavailable':
        return { form: t('lock.register.offline') };
      default:
        break;
    }
    return { form: describeError(e, 'login') };
  };

  const submit = async (e: FormEvent<HTMLFormElement>): Promise<void> => {
    e.preventDefault();
    if (busy) return;
    const next = check();
    setErrors(next);
    if (Object.keys(next).length > 0) return;
    setBusy(true);
    try {
      const res = await register({
        displayName: displayName.trim(),
        username: username.trim(),
        password,
        acceptRules: rules ? accepted : null,
        gender,
        birthDate: birthDateOf(birth),
      });
      push({
        id: 'self-signup',
        title: t('lock.register.title'),
        body: t('lock.register.welcome', { login: res.user.username }),
        level: 'success',
        source: 'system',
      });
    } catch (err) {
      setErrors(refusal(err));
      setPassword('');
      setAgain('');
    } finally {
      setBusy(false);
    }
  };

  return (
    <form
      onSubmit={(e) => void submit(e)}
      noValidate
      autoComplete="off"
      aria-label={t('lock.register.title')}
      className={clsx('flex flex-col gap-4', className)}
    >
      <p className="-mt-1 text-center text-sm text-muted">{t('lock.register.subtitle')}</p>
      <Input
        name="displayName"
        size="lg"
        autoFocus
        autoComplete="off"
        maxLength={NAME_MAX}
        label={t('lock.register.name')}
        placeholder={t('lock.register.namePlaceholder')}
        value={displayName}
        onChange={(e) => setDisplayName(e.target.value)}
        error={errors.displayName}
        disabled={busy}
      />
      <div role="radiogroup" aria-label={t('lock.register.gender')} className="flex flex-col gap-2">
        <span className="text-sm font-medium text-muted">{t('lock.register.gender')}</span>
        <div className="grid grid-cols-2 gap-3">
          {GENDERS.map((g) => (
            <button
              key={g}
              type="button"
              role="radio"
              aria-checked={gender === g}
              data-nav="true"
              disabled={busy}
              onClick={() => {
                setGender(g);
                setErrors((e) => ({ ...e, gender: undefined }));
              }}
              className={clsx(
                'focus-ring h-14 rounded-md border text-lg font-semibold transition-colors',
                gender === g
                  ? 'border-accent bg-accent/15 text-text'
                  : 'border-text/15 bg-text/[0.04] text-muted hover:bg-text/[0.08]',
              )}
            >
              {t(g === 'male' ? 'lock.register.genderMale' : 'lock.register.genderFemale')}
            </button>
          ))}
        </div>
        {errors.gender && (
          <p role="alert" className="text-sm font-medium text-danger">
            {errors.gender}
          </p>
        )}
      </div>
      <BirthDatePicker
        value={birth}
        disabled={busy}
        error={errors.birthDate}
        onChange={(p) => {
          setBirth(p);
          setErrors((e) => ({ ...e, birthDate: undefined }));
        }}
      />
      <Input
        name="username"
        size="lg"
        autoComplete="off"
        autoCapitalize="none"
        spellCheck={false}
        maxLength={LOGIN_MAX}
        label={t('lock.register.login')}
        placeholder={t('lock.register.loginPlaceholder')}
        hint={t('lock.register.loginHint')}
        value={username}
        onChange={(e) => setUsername(e.target.value)}
        error={errors.username}
        disabled={busy}
      />
      <div className="grid grid-cols-1 gap-4 sm:grid-cols-2">
        <Input
          name="password"
          size="lg"
          type="password"
          autoComplete="new-password"
          label={t('lock.register.password')}
          placeholder={t('lock.register.passwordPlaceholder')}
          value={password}
          onChange={(e) => setPassword(e.target.value)}
          error={errors.password}
          disabled={busy}
        />
        <Input
          name="passwordAgain"
          size="lg"
          type="password"
          autoComplete="new-password"
          label={t('lock.register.passwordAgain')}
          placeholder={t('lock.register.passwordAgainPlaceholder')}
          value={again}
          onChange={(e) => setAgain(e.target.value)}
          error={errors.passwordAgain}
          disabled={busy}
        />
      </div>
      {rules && (
        <section aria-label={t('lock.register.rulesTitle')} className="flex flex-col gap-2">
          <h2 className="text-sm font-medium text-muted">{t('lock.register.rulesTitle')}</h2>
          <ol className="themed-scrollbar max-h-28 list-decimal overflow-y-auto rounded-md bg-text/[0.04] py-2 pl-8 pr-3 text-sm text-text">
            {rules.map((line, i) => (
              <li key={i}>{line}</li>
            ))}
          </ol>
          <label className="flex cursor-pointer items-start gap-3 text-sm text-text">
            <input
              type="checkbox"
              name="acceptRules"
              data-nav="true"
              checked={accepted}
              disabled={busy}
              onChange={(e) => setAccepted(e.target.checked)}
              className="focus-ring mt-0.5 h-5 w-5 shrink-0 accent-[rgb(var(--c-accent))]"
            />
            <span>{t('lock.register.acceptRules')}</span>
          </label>
          {errors.acceptRules && (
            <p role="alert" className="text-sm font-medium text-danger">
              {errors.acceptRules}
            </p>
          )}
        </section>
      )}
      {errors.form && (
        <p role="alert" className="rounded-md bg-danger/15 px-4 py-3 text-base font-medium text-danger">
          {errors.form}
        </p>
      )}
      <Button type="submit" variant="cta" size="xl" block loading={busy}>
        {busy ? t('lock.register.submitting') : t('lock.register.submit')}
      </Button>
      <Button type="button" variant="ghost" size="lg" block disabled={busy} onClick={onCancel}>
        {t('lock.register.back')}
      </Button>
    </form>
  );
}

export default RegisterForm;
