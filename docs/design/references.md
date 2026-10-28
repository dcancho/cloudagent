# References

Related work, listed for context. Inclusion means the work is relevant, not that this design is
derived from it. Check the sources for exact claims.

## Agent memory and reflection

- Park, J. S., O'Brien, J. C., Cai, C. J., Morris, M. R., Liang, P., Bernstein, M. S. (2023).
  Generative Agents: Interactive Simulacra of Human Behavior. UIST '23.
  https://doi.org/10.1145/3586183.3606763 (arXiv:2304.03442).
  A memory stream with retrieval and periodic reflection.
- Packer, C., Wooders, S., Lin, K., Fang, V., Patil, S. G., Stoica, I., Gonzalez, J. E. (2023).
  MemGPT: Towards LLMs as Operating Systems. arXiv:2310.08560.
  https://arxiv.org/abs/2310.08560
  Paging between a limited context and external storage.
- Shinn, N., Cassano, F., Berman, E., Gopinath, A., Narasimhan, K., Yao, S. (2023). Reflexion:
  Language Agents with Verbal Reinforcement Learning. NeurIPS 2023. arXiv:2303.11366.
  https://arxiv.org/abs/2303.11366
  Learning from feedback through stored self-reflections.

## Retrieval

- Cormack, G. V., Clarke, C. L. A., Buettcher, S. (2009). Reciprocal Rank Fusion outperforms
  Condorcet and individual rank learning methods. SIGIR '09, pp. 758-759.
  https://doi.org/10.1145/1571941.1572114
  Combining several rankings by summing reciprocal ranks.
- Robertson, S., Zaragoza, H. (2009). The Probabilistic Relevance Framework: BM25 and Beyond.
  Foundations and Trends in Information Retrieval, 3(4), 333-389.
  https://doi.org/10.1561/1500000019
  The probabilistic basis of BM25 lexical ranking.

## Consolidation

- McClelland, J. L., McNaughton, B. L., O'Reilly, R. C. (1995). Why there are complementary
  learning systems in the hippocampus and neocortex: Insights from the successes and failures of
  connectionist models of learning and memory. Psychological Review, 102(3), 419-457.
  https://doi.org/10.1037/0033-295X.102.3.419
  The background for fast episodic storage consolidated into slower general knowledge.
- Reyna, V. F., Brainerd, C. J. (1995). Fuzzy-trace theory: An interim synthesis. Learning and
  Individual Differences, 7(1), 1-75. https://doi.org/10.1016/1041-6080(95)90031-4
  Verbatim and gist memory traces stored in parallel; the background for conversation gists.

## Prompt caching

- Anthropic. Prompt caching. Claude API documentation.
  https://platform.claude.com/docs/en/build-with-claude/prompt-caching
  The prefix-based caching behaviour (tools, then system, then messages) that the stable and
  ephemeral split is designed around.
