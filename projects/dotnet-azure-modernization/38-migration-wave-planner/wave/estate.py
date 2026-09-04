"""The estate: what is being migrated, and how it is wired together.

Declared as data rather than sampled, for the same reason project 43 generates
its corpus instead of collecting one -- the ground truth about coupling has to
be known exactly, because the whole planner is a claim about coupling. A random
graph would let the optimiser look good on structure that no real estate has.

The estate below is a mid-size general insurer at the point where somebody has
said "move it all to Azure". It is not a real company, but every shape in it is
one that turns up: a policy admin system nobody wants to touch, three
applications sharing one database because in 2009 that was the integration
strategy, a BizTalk hub, a file share that a nightly batch reads from, and a
reporting warehouse fed by replication.

Units are in these currencies throughout:
  effort        person-days of migration engineering
  money         GBP
  data_gb       gigabytes at rest
  traffic_gb_mo gigabytes per month crossing the edge
"""

from __future__ import annotations

from dataclasses import dataclass
from enum import Enum


class Kind(str, Enum):
    APP = "app"            # IIS-hosted web application
    SERVICE = "service"    # Windows service / console daemon
    DB = "db"              # SQL Server database
    FILESHARE = "fileshare"
    BATCH = "batch"        # scheduled job
    HUB = "hub"            # message broker / integration hub
    REPORTING = "reporting"


class Link(str, Enum):
    """How two components are coupled.

    The distinction that matters for planning is not what the traffic looks
    like, it is what happens if the two ends land in different waves.
    """

    SYNC = "sync_http"        # request/response; splitting adds WAN latency
    ASYNC = "async_queue"     # fire and forget; splitting is tolerable
    SHARED_DB = "shared_db"   # both ends write the same schema -- NOT splittable
    FILE = "file_share"       # one writes, one reads, via SMB
    BATCH = "batch_read"      # nightly bulk read
    REPLICA = "replica"       # transactional replication feed


#: Links that cannot be stretched across the migration boundary at any price.
#: Two components writing the same schema through a WAN gives you distributed
#: transactions, lock waits measured in seconds, and a class of corruption bug
#: nobody wants to debug at 2am. The planner treats these as a hard constraint
#: rather than an expensive edge, which is the whole reason unit contraction
#: exists (see wave/units.py and docs/adr/0001-shared-database-is-a-constraint.md).
UNSPLITTABLE = frozenset({Link.SHARED_DB})


@dataclass(frozen=True)
class Component:
    id: str
    name: str
    kind: Kind
    team: str
    #: 1 (back office, a day's outage is survivable) to 5 (customer-facing,
    #: an hour's outage is a regulatory event).
    criticality: int
    effort: float
    data_gb: float
    #: What the on-premises footprint costs per month, all in.
    onprem_monthly: float
    #: What the same workload costs per month once it is running in Azure.
    cloud_monthly: float
    #: Annual licence that is paid up front and is not refundable on
    #: decommission. This is the line item every migration business case
    #: forgets and every finance director remembers.
    licence_annual: float = 0.0
    #: Month of the licence year in which the term renews, 0 = January.
    licence_renews: int = 0
    notes: str = ""

    @property
    def dual_running_monthly(self) -> float:
        """Cost of having the workload live in both places for a month."""
        return self.onprem_monthly + self.cloud_monthly


@dataclass(frozen=True)
class Dependency:
    source: str
    target: str
    link: Link
    traffic_gb_mo: float
    #: Engineering cost of standing up a temporary hybrid link for this edge:
    #: VPN route, firewall change, a shim, and the testing of both.
    hybrid_build: float
    #: What that temporary link costs to run per month while the two ends are
    #: separated -- ExpressRoute share, monitoring, and the on-call attention
    #: that a hybrid link always attracts.
    hybrid_monthly: float
    #: For an unsplittable link only: what it costs to remove the coupling
    #: permanently -- replace direct schema access with an API or an event,
    #: backfill, dual-write, cut over, delete the grant. This is not a
    #: migration cost. It is remediation that has to happen *before* the
    #: migration can be planned at all, and the planner's job is to say when
    #: that is true rather than to quietly produce an infeasible schedule.
    decouple_effort: float = 0.0
    decouple_cost: float = 0.0

    @property
    def splittable(self) -> bool:
        return self.link not in UNSPLITTABLE


COMPONENTS: tuple[Component, ...] = (
    # --- policy administration: the system nobody wants to touch -----------
    Component("pas-core", "Policy Admin Core", Kind.APP, "policy", 5,
              95, 40, 4200, 2600, licence_annual=48000, licence_renews=3,
              notes="2004 vintage; the only place premium is calculated"),
    Component("pas-db", "Policy Admin DB", Kind.DB, "policy", 5,
              85, 2400, 6800, 4100, licence_annual=96000, licence_renews=3,
              notes="SQL Server Enterprise, 12 cores, 2.4 TB"),
    Component("pas-batch", "Nightly Renewal Batch", Kind.BATCH, "policy", 4,
              45, 12, 900, 480,
              notes="reads pas-db, writes the renewal extract to the file share"),
    Component("quote-web", "Quote & Buy Web", Kind.APP, "policy", 5,
              60, 8, 2100, 1200,
              notes="public; the only component with an external SLA"),
    Component("quote-rating", "Rating Engine", Kind.SERVICE, "policy", 5,
              75, 6, 1800, 900, licence_annual=22000, licence_renews=8),

    # --- claims -----------------------------------------------------------
    Component("claims-web", "Claims Portal", Kind.APP, "claims", 4,
              55, 14, 1900, 1100),
    Component("claims-svc", "Claims Workflow Service", Kind.SERVICE, "claims", 4,
              70, 9, 1600, 850),
    Component("claims-db", "Claims DB", Kind.DB, "claims", 4,
              65, 900, 3900, 2300, licence_annual=48000, licence_renews=3),
    Component("fnol-intake", "FNOL Intake API", Kind.APP, "claims", 5,
              40, 4, 1100, 620,
              notes="first notification of loss; 24x7"),
    Component("doc-store", "Claims Document Store", Kind.FILESHARE, "claims", 3,
              35, 6100, 2800, 1150,
              notes="6.1 TB of scanned PDFs on a Windows file server"),

    # --- the shared-database cluster: three apps, one schema --------------
    Component("billing-web", "Billing Portal", Kind.APP, "finance", 4,
              45, 5, 1400, 780),
    Component("billing-svc", "Direct Debit Service", Kind.SERVICE, "finance", 5,
              50, 3, 1200, 640,
              notes="BACS submission; a missed window is a customer-impacting event"),
    Component("collections", "Collections Workbench", Kind.APP, "finance", 3,
              40, 4, 1000, 560),
    Component("finance-db", "Finance DB", Kind.DB, "finance", 5,
              80, 1200, 5200, 3100, licence_annual=96000, licence_renews=3,
              notes="written by billing-web, billing-svc and collections directly"),
    Component("ledger-feed", "Ledger Feed", Kind.BATCH, "finance", 4,
              30, 8, 700, 380),

    # --- integration ------------------------------------------------------
    Component("esb", "Integration Hub", Kind.HUB, "platform", 5,
              110, 30, 5100, 2900, licence_annual=64000, licence_renews=10,
              notes="BizTalk; 41 orchestrations, 6 of them undocumented"),
    Component("mq", "Message Queue Cluster", Kind.HUB, "platform", 5,
              55, 20, 2400, 1300),
    Component("sftp-gw", "Partner SFTP Gateway", Kind.SERVICE, "platform", 4,
              25, 140, 800, 420),

    # --- customer & identity ---------------------------------------------
    Component("crm", "CRM", Kind.APP, "customer", 3,
              65, 220, 2600, 1500, licence_annual=54000, licence_renews=6),
    Component("crm-db", "CRM DB", Kind.DB, "customer", 3,
              50, 700, 3100, 1900, licence_annual=48000, licence_renews=6),
    Component("identity", "Identity Provider", Kind.SERVICE, "platform", 5,
              45, 2, 1500, 700,
              notes="ADFS; everything authenticates against it"),
    Component("portal", "Customer Self-Service Portal", Kind.APP, "customer", 5,
              70, 10, 2300, 1300),

    # --- reporting and data ----------------------------------------------
    Component("dw", "Data Warehouse", Kind.REPORTING, "data", 2,
              95, 4800, 6100, 3400, licence_annual=96000, licence_renews=3),
    Component("etl", "Nightly ETL", Kind.BATCH, "data", 2,
              60, 40, 1300, 700),
    Component("bi", "BI Server", Kind.REPORTING, "data", 2,
              40, 90, 2200, 1100, licence_annual=38000, licence_renews=0),
    Component("reg-report", "Regulatory Reporting", Kind.BATCH, "data", 5,
              50, 25, 900, 500,
              notes="Solvency II submission; a late submission is reportable"),

    # --- shared infrastructure -------------------------------------------
    Component("print-svc", "Print & Fulfilment Service", Kind.SERVICE, "platform", 3,
              30, 55, 900, 500),
    Component("outbound-share", "Outbound File Share", Kind.FILESHARE, "platform", 3,
              20, 300, 1100, 380),
    Component("scheduler", "Job Scheduler", Kind.SERVICE, "platform", 4,
              25, 3, 600, 320,
              notes="Control-M; sequences 140 jobs across every team"),
)


DEPENDENCIES: tuple[Dependency, ...] = (
    # policy
    Dependency("pas-core", "pas-db", Link.SHARED_DB, 0, 0, 0, 55, 46000),
    Dependency("pas-batch", "pas-db", Link.SHARED_DB, 0, 0, 0, 30, 24000),
    Dependency("quote-web", "quote-rating", Link.SYNC, 40, 9000, 900),
    Dependency("quote-rating", "pas-db", Link.SYNC, 26, 14000, 1400),
    Dependency("quote-web", "pas-core", Link.SYNC, 18, 9000, 900),
    Dependency("pas-batch", "outbound-share", Link.FILE, 95, 7000, 600),
    Dependency("pas-core", "esb", Link.ASYNC, 22, 6000, 500),
    Dependency("pas-core", "identity", Link.SYNC, 2, 5000, 400),
    Dependency("quote-web", "identity", Link.SYNC, 3, 5000, 400),

    # claims
    Dependency("claims-web", "claims-svc", Link.SYNC, 31, 9000, 900),
    Dependency("claims-svc", "claims-db", Link.SHARED_DB, 0, 0, 0, 45, 38000),
    Dependency("claims-web", "claims-db", Link.SHARED_DB, 0, 0, 0, 35, 29000),
    Dependency("fnol-intake", "claims-svc", Link.SYNC, 14, 9000, 900),
    Dependency("fnol-intake", "mq", Link.ASYNC, 9, 5000, 450),
    Dependency("claims-svc", "doc-store", Link.FILE, 310, 11000, 1600),
    Dependency("claims-svc", "esb", Link.ASYNC, 17, 6000, 500),
    Dependency("claims-web", "identity", Link.SYNC, 2, 5000, 400),
    Dependency("claims-svc", "pas-db", Link.SYNC, 21, 14000, 1400),

    # the shared-database cluster
    Dependency("billing-web", "finance-db", Link.SHARED_DB, 0, 0, 0, 40, 34000),
    Dependency("billing-svc", "finance-db", Link.SHARED_DB, 0, 0, 0, 60, 52000),
    Dependency("collections", "finance-db", Link.SHARED_DB, 0, 0, 0, 28, 23000),
    Dependency("ledger-feed", "finance-db", Link.SHARED_DB, 0, 0, 0, 22, 18000),
    Dependency("billing-svc", "sftp-gw", Link.FILE, 22, 7000, 600),
    Dependency("billing-web", "pas-core", Link.SYNC, 12, 9000, 900),
    Dependency("billing-svc", "esb", Link.ASYNC, 8, 6000, 500),
    Dependency("collections", "crm", Link.SYNC, 6, 8000, 750),
    Dependency("billing-web", "identity", Link.SYNC, 1, 5000, 400),

    # integration hub fan-out: the reason the ESB is expensive to move
    Dependency("esb", "mq", Link.ASYNC, 46, 6000, 550),
    Dependency("esb", "crm", Link.SYNC, 11, 8000, 750),
    Dependency("esb", "print-svc", Link.ASYNC, 19, 6000, 500),
    Dependency("esb", "sftp-gw", Link.FILE, 28, 7000, 600),
    Dependency("esb", "claims-db", Link.SYNC, 13, 14000, 1400),
    Dependency("esb", "finance-db", Link.SYNC, 15, 14000, 1400),
    Dependency("esb", "dw", Link.ASYNC, 34, 6000, 500),

    # customer
    Dependency("crm", "crm-db", Link.SHARED_DB, 0, 0, 0, 30, 26000),
    Dependency("portal", "pas-core", Link.SYNC, 24, 9000, 900),
    Dependency("portal", "claims-web", Link.SYNC, 7, 9000, 900),
    Dependency("portal", "billing-web", Link.SYNC, 10, 9000, 900),
    Dependency("portal", "identity", Link.SYNC, 5, 5000, 400),
    Dependency("crm", "identity", Link.SYNC, 1, 5000, 400),

    # reporting: high volume, low criticality -- the cheapest edges to split
    Dependency("etl", "pas-db", Link.BATCH, 420, 4000, 260),
    Dependency("etl", "claims-db", Link.BATCH, 260, 4000, 260),
    Dependency("etl", "finance-db", Link.BATCH, 190, 4000, 260),
    Dependency("etl", "crm-db", Link.BATCH, 140, 4000, 260),
    Dependency("etl", "dw", Link.SHARED_DB, 0, 0, 0, 38, 31000),
    Dependency("bi", "dw", Link.SYNC, 88, 9000, 900),
    Dependency("reg-report", "dw", Link.BATCH, 60, 4000, 260),
    Dependency("dw", "pas-db", Link.REPLICA, 310, 12000, 1500),

    # shared infrastructure
    Dependency("print-svc", "outbound-share", Link.FILE, 180, 7000, 600),
    Dependency("scheduler", "pas-batch", Link.BATCH, 1, 3000, 200),
    Dependency("scheduler", "etl", Link.BATCH, 1, 3000, 200),
    Dependency("scheduler", "ledger-feed", Link.BATCH, 1, 3000, 200),
    Dependency("scheduler", "reg-report", Link.BATCH, 1, 3000, 200),
    Dependency("sftp-gw", "outbound-share", Link.FILE, 90, 7000, 600),
)


#: Governance limit on the length of a wave. Longer than this and it stops
#: being a wave -- the property that makes waves useful is a decision point at
#: the end of each one, and nobody stops a programme at a checkpoint nine
#: months after the last one.
MAX_WAVE_MONTHS = 2.5

#: Person-days a team can commit to migration work per *month*, after the
#: run-the-bank work it cannot stop doing. This is the real constraint on any
#: migration: the cloud will accept work far faster than an insurer can supply
#: it, so every "we could do this in six months" conversation is a conversation
#: about these six numbers whether or not anybody says so.
TEAM_RATE: dict[str, float] = {
    "policy": 40,
    "claims": 48,
    "finance": 52,
    "platform": 46,
    "customer": 48,
    "data": 64,
}


@dataclass(frozen=True)
class Estate:
    components: tuple[Component, ...]
    dependencies: tuple[Dependency, ...]
    rate: dict[str, float]

    def by_id(self, cid: str) -> Component:
        return self._index[cid]

    @property
    def _index(self) -> dict[str, Component]:
        return {c.id: c for c in self.components}

    @property
    def capacity(self) -> dict[str, float]:
        """Person-days a team can supply inside one wave."""
        return {t: r * MAX_WAVE_MONTHS for t, r in self.rate.items()}

    @property
    def total_effort(self) -> float:
        return sum(c.effort for c in self.components)

    @property
    def wave_capacity(self) -> float:
        """Total person-days deliverable in one wave across all teams."""
        return sum(self.capacity.values())

    @property
    def floor_months(self) -> float:
        """Shortest programme any arrangement can achieve.

        Waves run in sequence and teams run in parallel inside a wave, so the
        total duration is the sum over waves of the busiest team in each wave.
        That sum is at least the busiest team's total work divided by its rate,
        whatever the arrangement. One team sets the floor, and no amount of
        wave design moves it.
        """
        per_team: dict[str, float] = {}
        for c in self.components:
            per_team[c.team] = per_team.get(c.team, 0.0) + c.effort
        return max(e / self.rate[t] for t, e in per_team.items())

    def neighbours(self, cid: str) -> list[Dependency]:
        return [d for d in self.dependencies if d.source == cid or d.target == cid]


def build_estate() -> Estate:
    e = Estate(COMPONENTS, DEPENDENCIES, dict(TEAM_RATE))
    assert_estate_is_consistent(e)
    return e


# ---------------------------------------------------------------------------
# Invariants. These run at construction, not in a test, because every number
# downstream is computed from this data and a malformed estate produces a
# plausible plan rather than an error.
# ---------------------------------------------------------------------------


def assert_estate_is_consistent(e: Estate) -> None:
    ids = {c.id for c in e.components}
    if len(ids) != len(e.components):
        raise ValueError("duplicate component id")

    for d in e.dependencies:
        if d.source not in ids:
            raise ValueError(f"dependency from unknown component {d.source!r}")
        if d.target not in ids:
            raise ValueError(f"dependency to unknown component {d.target!r}")
        if d.source == d.target:
            raise ValueError(f"self-dependency on {d.source!r}")

    seen: set[tuple[str, str]] = set()
    for d in e.dependencies:
        key = (d.source, d.target)
        if key in seen:
            raise ValueError(f"duplicate dependency {key}")
        seen.add(key)

    for c in e.components:
        if c.team not in e.rate:
            raise ValueError(f"component {c.id!r} on team {c.team!r} with no capacity")
        if c.effort <= 0:
            raise ValueError(f"component {c.id!r} has non-positive effort")
        if c.cloud_monthly > c.onprem_monthly:
            raise ValueError(
                f"component {c.id!r} costs more in cloud than on-prem; the "
                "planner's savings model assumes otherwise and would report "
                "a negative benefit as a positive one"
            )
        if not 1 <= c.criticality <= 5:
            raise ValueError(f"component {c.id!r} criticality out of range")

    # An unsplittable edge that carries a hybrid cost is a contradiction: the
    # planner will never split it, so the cost is dead data that would mislead
    # anyone reading the estate.
    for d in e.dependencies:
        if not d.splittable and (d.hybrid_build or d.hybrid_monthly or d.traffic_gb_mo):
            raise ValueError(
                f"unsplittable dependency {d.source}->{d.target} carries split "
                "costs, which can never be incurred"
            )
        if d.splittable and (d.decouple_effort or d.decouple_cost):
            raise ValueError(
                f"splittable dependency {d.source}->{d.target} carries a "
                "decoupling cost; there is nothing to decouple"
            )
        if not d.splittable and not (d.decouple_effort and d.decouple_cost):
            raise ValueError(
                f"unsplittable dependency {d.source}->{d.target} has no "
                "decoupling cost, so the planner cannot price the remediation "
                "that would make an infeasible estate feasible"
            )

    # Every team must be able to move its single largest component inside one
    # wave, or no feasible plan exists and the planner would loop looking for
    # one.
    for c in e.components:
        if c.effort > e.capacity[c.team]:
            raise ValueError(
                f"component {c.id!r} needs {c.effort} days but team "
                f"{c.team!r} can only supply {e.capacity[c.team]} per wave"
            )
