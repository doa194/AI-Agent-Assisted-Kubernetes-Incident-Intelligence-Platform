# Testing

Testing must protect important system behavior, not maximize test count or code coverage.

## Core Rules

* Choose the **cheapest reliable test layer** for each behavior.
* Test important behaviors, contracts, failure modes, state transitions, and edge cases.
* Do not create tests simply because a class, function, file, or manifest exists.
* Avoid testing implementation details unless they represent an important contract.
* Do not duplicate the same behavior across multiple test layers unless each test protects against a different failure mode.
* Prefer deterministic, isolated, repeatable, and easy-to-understand tests.
* Use Arrange-Act-Assert or another equally clear structure where appropriate.
* Do not create low-value tests solely to increase code coverage.

## Test Layers

Use the following hierarchy as a guideline, not as a fixed quota:

1. **Unit Tests**

   * Prefer for pure logic, validation, calculations, policies, state transitions, and important edge cases.
   * Keep them fast and independent from external infrastructure.

2. **Integration Tests**

   * Use when behavior depends on databases, message brokers, caches, filesystems, containers, or other real infrastructure.
   * Prefer real dependencies or disposable containers when practical instead of excessive mocking.

3. **Component / API Tests**

   * Use for important service boundaries, API contracts, authentication/authorization behavior, and externally visible behavior.
   * Keep their number smaller than unit and integration tests.

4. **End-to-End Tests**

   * Use only for critical workflows that cross multiple system components.
   * Keep E2E coverage intentionally small because these tests are slower and more expensive to maintain.
   * Do not reproduce lower-level test coverage through E2E tests.

5. **Infrastructure / Operational Tests**

   * Use when the project contains Docker, Kubernetes, networking, security policies, observability, deployment automation, or similar infrastructure.
   * Verify actual behavior such as deployment health, readiness, connectivity, access restrictions, recovery, metrics, and service availability.
   * Do not create artificial unit tests for configuration files when the real platform can validate the behavior more reliably.

## Test Distribution

As a general preference:

* Many meaningful Unit Tests
* A moderate number of Integration Tests
* A small number of Component/API Tests
* Very few critical-path E2E Tests
* Infrastructure/Operational Tests where relevant

This is a heuristic, not a required ratio. Let architecture, risk, and system behavior determine the actual distribution.

## Verification

After implementation:

* Run all relevant automated tests.
* Fix failures caused by the implementation.
* Verify critical infrastructure and runtime behavior using appropriate operational checks.
* Test important recovery and failure scenarios where relevant.

A project or feature is not complete merely because Unit Tests pass. All test layers and operational checks required by the project's architecture and requirements must pass.

# Documentation

Documentation must describe the **finished project**, not the process used to build it.

## General Rules

* Treat the implemented and verified repository as the source of truth.
* Do not use roadmap phases, implementation steps, task progress, or development chronology as documentation structure.
* Do not include implementation progress, phase completion summaries, agent activity, or descriptions of what was done during each roadmap phase.
* Document the resulting architecture and behavior, not the process used to create them.
* Do not describe planned, missing, optional, or experimental features as implemented.
* Keep documentation consistent with the actual code, configuration, infrastructure, commands, and project structure.
* Use simple language and explain important technical terms when necessary.
* Explain important design choices and trade-offs, not just what technologies are used.
* Avoid duplicated information. Link to detailed documentation instead of repeating it.

## Documentation Workflow

During implementation, update documentation only when necessary to preserve important technical decisions or required developer information.

After implementation, testing, and operational verification are complete, perform a dedicated documentation pass:

1. Inspect the final repository and implemented features.
2. Verify the actual architecture, project structure, configuration, commands, and workflows.
3. Create or update `README.md`.
4. Create or update only the `/docs` documents justified by the project's complexity.
5. Verify documentation against the running project.
6. Remove stale, speculative, duplicated, roadmap-oriented, or progress-oriented content.

## README.md

Keep the root README focused on project discovery and onboarding.

It should help a new reader quickly understand:

* What the project is
* Why it exists
* What problem it solves
* Main features
* Main technologies
* High-level architecture
* How to set it up
* How to run it
* How to test and verify it
* How to stop or clean it up
* Important limitations
* Where to find deeper documentation

Keep detailed architecture explanations, design decisions, testing strategy, production considerations, and other deep technical material in `/docs`.

Do not turn the README into a development diary, roadmap, or implementation report.

## `/docs`

Create documentation files based on actual project needs rather than a fixed checklist.

Possible documents include:

* `architecture.md`
* `setup-guide.md`
* `testing-strategy.md`
* `design-decisions.md`
* `security.md`
* `observability.md`
* `troubleshooting.md`
* `production-considerations.md`
* `codebase-guide.md`

Create only documents that provide meaningful information for the current project.

Organize documentation around questions and system concerns rather than creating one document for every technology or repository directory.

## Setup and Usage

Setup instructions must be reproducible and include relevant prerequisites, configuration, environment variables, startup commands, verification steps, testing commands, and cleanup commands.

Commands should be ready to copy and run whenever practical.

Do not assume the reader already understands the repository structure.

## Architecture and Design

Document important architectural decisions and explain:

* What approach was selected
* Why it was selected
* Important alternatives considered
* Major benefits
* Important trade-offs and limitations

Use Mermaid diagrams when they materially improve understanding.

Keep diagrams simple and ensure they match the actual implementation.

## Production Considerations

Clearly distinguish between:

* What is implemented now
* What is intentionally simplified for local development
* What would need to change for production use

Do not present production recommendations as currently implemented functionality.

## Final Verification

Before considering documentation complete, verify that:

* Commands work
* File paths and internal links are correct
* Documented features exist
* Diagrams match the implementation
* Setup instructions are reproducible
* Testing instructions match the actual test suites
* Known limitations are stated honestly
* Production recommendations are separated from current behavior
* No roadmap phases, progress reports, implementation chronology, or agent-generated task summaries remain in project documentation


## Code Explanation and Commenting Rules

1. Explain the purpose of every important file using short, simple comments near the top of the file.

2. Do not use XML documentation blocks such as:

```text
/// <summary>
/// <param>
/// <returns>
```

3. Use normal comments supported by the language instead.

4. Write comments in simple language that is easy for a learner to understand.

5. Avoid complicated technical terms. When a technical term is necessary, explain it in plain language.

6. Explain why important code exists, not only what the code does.

7. Add comments for:

   * important rules
   * unusual decisions
   * failure handling
   * security behavior
   * interactions between components
   * code that may not be immediately clear to a learner

8. Do not comment obvious code such as:

   * simple assignments
   * basic constructors
   * getters and setters
   * imports or namespace declarations
   * self-explanatory method calls

9. Do not comment every line.

10. Keep comments short, focused, and useful.

11. Prefer clear names and small methods over long comments.

12. Refactor confusing code instead of using comments to hide unnecessary complexity.

13. Update or remove comments whenever the related code changes.

14. Never leave comments that disagree with the current implementation.

15. For each major file, make clear:

* why the file exists
* what it is responsible for
* how it fits into the project

16. Keep project documentation updated when file responsibilities, workflows, or project structure change.

The goal is not to create the largest number of tests or comments.

The goal is to create code that is reliable, understandable, easy to maintain, and efficient to validate.