"""A synthetic support-assistant corpus.

Six topics, each with question and answer templates and a vocabulary. The generator is
seeded and deterministic: the same seed produces the same traffic, which is what makes a
detection-delay measurement reproducible.

The point of generating *text* rather than vectors directly is that every degradation in
stream.py has to express itself the way a real one would -- by changing what the model
says -- and then be discovered by a detector that only sees embeddings. A simulator that
injected drift straight into the vectors would be measuring its own arithmetic.
"""

from __future__ import annotations

import random
from dataclasses import dataclass

TOPICS: tuple[str, ...] = (
    "billing",
    "shipping",
    "returns",
    "account",
    "technical",
    "warranty",
)

QUESTION_TEMPLATES: dict[str, tuple[str, ...]] = {
    "billing": (
        "I was charged {amount} twice on my {card} card this month, can you explain the duplicate?",
        "Why does my invoice show a {amount} line item I did not authorise for order {order}?",
        "My subscription renewed at {amount} but the plan page advertises a lower price.",
    ),
    "shipping": (
        "Order {order} has been in transit for {days} days with no scan, where is it?",
        "The courier marked order {order} delivered but nothing arrived at my address.",
        "Can I change the delivery address on order {order} before it leaves the warehouse?",
    ),
    "returns": (
        "I want to return order {order}, the item arrived damaged in the packaging.",
        "How long do I have to return an item bought on offer, order {order}?",
        "I sent back order {order} {days} days ago and have not seen the {amount} refund.",
    ),
    "account": (
        "I cannot sign in, the reset email for my account never arrives.",
        "How do I merge two accounts that both have orders including {order}?",
        "Please close my account and delete the payment card ending {card}.",
    ),
    "technical": (
        "The app crashes on launch after the latest update on my device.",
        "Sync has been stuck for {days} days and my orders do not appear.",
        "Two factor codes are rejected even though the clock on my phone is correct.",
    ),
    "warranty": (
        "Is the item in order {order} still covered {days} days after purchase?",
        "The product failed within the warranty period, what evidence do you need?",
        "Does the warranty transfer if I gift the item bought on order {order}?",
    ),
}

ANSWER_TEMPLATES: dict[str, tuple[str, ...]] = {
    "billing": (
        "I can see the duplicate charge of {amount} against order {order}. I have raised a "
        "reversal with the payment provider and it will settle to the card ending {card} "
        "within five working days. You do not need to do anything further.",
        "That line item is the pro rata charge for the plan change you made on the {day}th. "
        "The {amount} covers the remainder of the current period; next month returns to the "
        "advertised rate. I have attached the itemised breakdown to your account.",
    ),
    "shipping": (
        "Order {order} left the depot but the carrier has not scanned it for {days} days, "
        "which usually means it was mis-sorted. I have opened a trace with the courier and "
        "dispatched a replacement today so you are not waiting on the outcome.",
        "The address on order {order} can still be changed because it has not been picked. "
        "I have updated it to the one on your account and the change will show on the "
        "tracking page within the hour.",
    ),
    "returns": (
        "I am sorry order {order} arrived damaged. A prepaid return label is on its way to "
        "your email, and the {amount} refund will be issued as soon as the carrier scans "
        "the parcel rather than when it reaches the warehouse.",
        "Your return for order {order} was received but the refund was held by an automated "
        "check. I have released it manually; the {amount} will be back on the card ending "
        "{card} within three working days.",
    ),
    "account": (
        "The reset email was being rejected by your provider rather than not sent. I have "
        "switched your account to a one time code and sent it now. Once you are in, please "
        "add a recovery address so this cannot recur.",
        "I can merge the two accounts and keep the order history from both, including order "
        "{order}. The merge takes effect immediately and the duplicate account is closed "
        "rather than deleted so the invoices remain available to you.",
    ),
    "technical": (
        "The crash on launch is caused by a cached layout from the previous version. "
        "Clearing the app cache resolves it without signing you out, and the fix ships in "
        "the release going out this week so it will not recur.",
        "Sync has been stuck for {days} days because a background token expired while the "
        "device was offline. I have invalidated it server side; the next launch will "
        "re-authenticate and backfill the missing orders.",
    ),
    "warranty": (
        "Order {order} is {days} days old, which is inside the two year cover. Send a photo "
        "of the fault and the serial number and I will raise the claim without you needing "
        "the original receipt, since the purchase is on your account.",
        "The warranty follows the item rather than the buyer, so the cover on order {order} "
        "transfers with the gift. The recipient should quote the order number when they "
        "claim and we will treat it as the original purchase.",
    ),
}

REFUSAL_TEMPLATES: tuple[str, ...] = (
    "I am not able to help with that request. Please contact support through another channel.",
    "I cannot assist with this. You may want to speak to a member of our team directly.",
    "Sorry, that is outside what I am able to do here. A colleague can pick this up for you.",
)

# What a cheaper or heavily quantised model produces: shorter, structurally identical,
# and entirely free of the specifics that make the original answer useful.
GENERIC_TEMPLATES: tuple[str, ...] = (
    "Thanks for getting in touch. I have looked at your account and the issue should be "
    "resolved shortly. Please let me know if you need anything else.",
    "I understand your concern and I am sorry for the trouble. Our team is aware and it "
    "will be sorted out for you soon.",
    "Thank you for your patience. I have made a note on your account and things should be "
    "back to normal shortly.",
)


@dataclass(frozen=True)
class Turn:
    """One request/response pair, with the ground truth a real system would not have."""

    day: int
    topic: str
    question: str
    answer: str
    latency_ms: float
    http_status: int
    #: Ground truth. Only the evaluator sees this; no detector is allowed to.
    is_degraded: bool
    is_refusal: bool
    quality: float


def _fill(template: str, rng: random.Random) -> str:
    return template.format(
        amount=f"{rng.randrange(5, 400)}.{rng.randrange(0, 100):02d}",
        order=f"{rng.randrange(100000, 999999)}",
        card=f"{rng.randrange(1000, 9999)}",
        days=rng.randrange(2, 45),
        day=rng.randrange(1, 28),
    )


def question(topic: str, rng: random.Random) -> str:
    return _fill(rng.choice(QUESTION_TEMPLATES[topic]), rng)


def answer(topic: str, rng: random.Random) -> str:
    return _fill(rng.choice(ANSWER_TEMPLATES[topic]), rng)


def refusal(rng: random.Random) -> str:
    return rng.choice(REFUSAL_TEMPLATES)


def generic_answer(rng: random.Random) -> str:
    return rng.choice(GENERIC_TEMPLATES)


def stale_answer(topic: str, rng: random.Random) -> str:
    """A confidently wrong answer: right register, wrong topic.

    This is what retrieval decay looks like from the outside. The model is fine; it was
    handed documents about something else and wrote a fluent answer about them. Crucially
    the *style* is unchanged, which is why length and perplexity proxies miss it.
    """
    others = [t for t in TOPICS if t != topic]
    return _fill(rng.choice(ANSWER_TEMPLATES[rng.choice(others)]), rng)


def truncated_answer(topic: str, rng: random.Random) -> str:
    """A prompt template that lost a field: the answer starts correctly and stops.

    Models do this when an instruction is missing rather than contradicted -- the output
    is well formed and simply does less.
    """
    full = answer(topic, rng)
    sentences = full.split(". ")
    return sentences[0] + "."
