CREATE OR REPLACE VIEW Customer AS
SELECT stack(5,
    1, 'Nikola Tesla', DATE '1856-07-10',
    2, 'Albert Einstein', DATE '1879-03-14',
    3, 'John von Neumann', DATE '1903-12-28',
    4, 'Alan Turing', DATE '1912-06-23',
    5, 'Linus Torvalds', DATE '1969-12-28'
) AS (CustomerId, FullName, BirthDate);
