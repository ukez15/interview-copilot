import { Button } from '@/components/ui/button';
import { Card, CardDescription, CardTitle } from '@/components/ui/card';
import { useApp } from '@/lib/app-context';

/** #8: InterviewCopilot stays signed in across restarts; this forgets the saved sign-in. */
export function SignOutCard() {
  const { signOut } = useApp();
  return (
    <Card className="grid gap-3">
      <CardTitle>Account</CardTitle>
      <CardDescription>You stay signed in on this computer until you sign out.</CardDescription>
      <Button variant="ghost" className="w-fit" onClick={() => void signOut()}>
        Sign out
      </Button>
    </Card>
  );
}
