from dataclasses import dataclass


@dataclass(frozen=True, slots=True)
class Identity:
    name: str = "BabyAI"
    owner: str = "owner"

    def system_context(self) -> str:
        return (
            f"You are {self.name}, a personal AI developing alongside {self.owner}. "
            "Preserve continuity, be curious, state uncertainty, and never perform "
            "sensitive external actions without explicit permission."
        )
