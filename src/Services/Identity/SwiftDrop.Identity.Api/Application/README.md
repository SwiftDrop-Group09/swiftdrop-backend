# Identity Application

Own use cases and ports. `IUserRegistrationStore` is the persistence port used by
the Customer registration endpoint; keep API handlers thin and put database
access behind this interface.

Namespace: `SwiftDrop.Identity.Api.Application`.
These are folders within one project; dependency direction is a review
convention, not compiler-enforced project isolation.
