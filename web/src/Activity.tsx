import { Icon } from './lucent'

export type Activity = {
  role: 'activity'
  tool: string
  ok: boolean
  summary: string
  path?: string | null
  before?: string | null
  after?: string | null
}

function lines(text: string): string[] {
  return text.replace(/\r\n/g, '\n').replace(/\n$/, '').split('\n')
}

function Diff({ before, after }: { before: string; after: string }) {
  return (
    <pre className="activity__diff">
      {lines(before).map((line, i) => (
        <span key={`b${i}`} className="activity__diff-line activity__diff-line--removed">
          {'- '}
          {line}
          {'\n'}
        </span>
      ))}
      {after !== '' &&
        lines(after).map((line, i) => (
          <span key={`a${i}`} className="activity__diff-line activity__diff-line--added">
            {'+ '}
            {line}
            {'\n'}
          </span>
        ))}
    </pre>
  )
}

function Step({ step }: { step: Activity }) {
  const head = (
    <>
      <Icon name={step.ok ? 'check' : 'error_outline'} size="sm" className={step.ok ? 'activity__ok' : 'activity__failed'} />
      <span className="activity__tool">{step.tool}</span>
      <span className="activity__summary">{step.summary}</span>
    </>
  )

  if (step.before == null || step.after == null) return <li className="activity__step">{head}</li>

  return (
    <li className="activity__step">
      <details className="activity__edit">
        <summary>{head}</summary>
        <Diff before={step.before} after={step.after} />
      </details>
    </li>
  )
}

export function ActivitySteps({ steps }: { steps: Activity[] }) {
  const last = steps[steps.length - 1]
  const failed = steps.filter((s) => !s.ok).length
  return (
    <details className="activity">
      <summary className="activity__header">
        <Icon name="build" size="sm" />
        <span className="activity__count">
          {steps.length} {steps.length === 1 ? 'step' : 'steps'}
          {failed > 0 && `, ${failed} failed`}
        </span>
        <span className="activity__summary">
          {last.tool} {last.summary}
        </span>
      </summary>
      <ol className="activity__steps">
        {steps.map((step, i) => (
          <Step key={i} step={step} />
        ))}
      </ol>
    </details>
  )
}
