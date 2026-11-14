"""One deadline for test work, with a separate bounded cleanup reserve."""
import time


class Budget:
    def __init__(self, failure, clock=time.monotonic, work_seconds=600, cleanup_seconds=90):
        self.failure = failure
        self.clock = clock
        self.work_deadline = clock() + work_seconds
        self.cleanup_deadline = self.work_deadline + cleanup_seconds
        self.cleaning = False

    def timeout(self, maximum):
        deadline = self.cleanup_deadline if self.cleaning else self.work_deadline
        remaining = deadline - self.clock()
        if remaining <= 0:
            code = "test_cleanup_budget_exhausted" if self.cleaning else "test_verification_budget_exhausted"
            raise self.failure(code)
        return min(maximum, remaining)

    def check(self):
        self.timeout(1)

    def sleep(self, seconds, sleeper=time.sleep):
        sleeper(self.timeout(seconds))

    def begin_cleanup(self):
        # Never move the deadline forward; a late phase cannot extend the job.
        self.cleaning = True
