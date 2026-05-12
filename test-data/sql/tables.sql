CREATE TABLE customers (
    customer_id   NUMBER(10)    NOT NULL,
    first_name    VARCHAR2(50)  NOT NULL,
    last_name     VARCHAR2(50)  NOT NULL,
    email         VARCHAR2(100) NOT NULL,
    created_date  DATE          NOT NULL,
    CONSTRAINT pk_customers PRIMARY KEY (customer_id)
);

CREATE TABLE orders (
    order_id      NUMBER(10)    NOT NULL,
    customer_id   NUMBER(10)    NOT NULL,
    order_date    DATE          NOT NULL,
    total_amount  NUMBER(12,2)  NOT NULL,
    status        VARCHAR2(20)  NOT NULL,
    CONSTRAINT pk_orders          PRIMARY KEY (order_id),
    CONSTRAINT fk_orders_customer FOREIGN KEY (customer_id) REFERENCES customers (customer_id)
);

CREATE TABLE order_items (
    item_id       NUMBER(10)    NOT NULL,
    order_id      NUMBER(10)    NOT NULL,
    product_code  VARCHAR2(20)  NOT NULL,
    quantity      NUMBER(5)     NOT NULL,
    unit_price    NUMBER(12,2)  NOT NULL,
    CONSTRAINT pk_order_items   PRIMARY KEY (item_id),
    CONSTRAINT fk_items_order   FOREIGN KEY (order_id) REFERENCES orders (order_id)
);

CREATE TABLE products (
    product_code  VARCHAR2(20)   NOT NULL,
    product_name  VARCHAR2(100)  NOT NULL,
    category      VARCHAR2(50)   NOT NULL,
    list_price    NUMBER(12,2)   NOT NULL,
    active_flag   CHAR(1)        DEFAULT 'Y' NOT NULL,
    CONSTRAINT pk_products PRIMARY KEY (product_code)
);
