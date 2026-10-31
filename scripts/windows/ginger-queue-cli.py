"""Fixed checkout entry point; avoids shell-quoted Python command strings."""
from pathlib import Path
import sys

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / 'src'))
from babyai.ginger_price_queue import main

if __name__ == '__main__':
    main()
