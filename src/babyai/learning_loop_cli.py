import typer

from .autodidact import LessonCandidateStore
from .config import BabyAIConfig
from .curiosa import CuriosityStore
from .evidence import EvidenceStore
from .hypothesis import HypothesisStore
from .learning_loop import LearningLoop
from .working_memory import WorkingMemoryStore

app = typer.Typer(help="Inspect BabyAI's explicit learning loop")


@app.command("status")
def status() -> None:
    config = BabyAIConfig.default()
    snapshot = LearningLoop.evaluate(
        WorkingMemoryStore(config.working_memory_file).load(),
        HypothesisStore(config.hypothesis_file).load(),
        EvidenceStore(config.evidence_file).load(),
        CuriosityStore(config.curiosity_file).load(),
        LessonCandidateStore(config.lesson_candidate_file).load(),
    )
    typer.echo(snapshot.as_context())


if __name__ == "__main__":
    app()
